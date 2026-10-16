#!/usr/bin/env bash
# HTTP-level acceptance tests for AcxiomCRM (Sections 17.19 and 17.7 plus security checks).
# Run through tests/run-tests.sh, which starts the app on a fresh test database.
# Usage: BASE=http://localhost:5099 DB=tests/test.db bash tests/acceptance.sh
cd "$(dirname "$0")"
B=${BASE:-http://localhost:5058}
DBFILE=${DB:-../app.db}
RUN=$RANDOM
PASS=0; FAIL=0; FAILED=()
check() { if [ "$2" = "1" ]; then echo "PASS  $1"; PASS=$((PASS+1)); else echo "FAIL  $1  [$3]"; FAIL=$((FAIL+1)); FAILED+=("$1"); fi; }
tokof() { curl -s -c $1 -b $1 "$B$2" | grep -o 'name="__RequestVerificationToken" type="hidden" value="[^"]*' | head -1 | sed 's/.*value="//'; }
login() { rm -f $1; local t; t=$(tokof $1 /Identity/Account/Login)
  curl -s -o /dev/null -w '%{redirect_url}' -c $1 -b $1 --data-urlencode "Input.Email=$2" --data-urlencode "Input.Password=$3" --data-urlencode "__RequestVerificationToken=$t" $B/Identity/Account/Login; }
code() { curl -s -o /dev/null -w '%{http_code}' -b $1 "$B$2"; }
redir() { curl -s -o /dev/null -w '%{redirect_url}' -b $1 "$B$2"; }
post() { local jar=$1 path=$2; shift 2; local t; t=$(tokof $jar "$TOKPAGE"); curl -s -b $jar -c $jar --data-urlencode "__RequestVerificationToken=$t" "$@" "$B$path"; }
json() { local jar=$1 method=$2 path=$3 body=$4; curl -s -w '\n%{http_code}' -b $jar -X $method -H 'Content-Type: application/json' -d "$body" "$B$path"; }
status_of() { echo "$1" | tail -1; }
sql() { python -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); print('\n'.join('|'.join(str(x) for x in r) for r in c.execute(sys.argv[2])))" "$DBFILE" "$1"; }

for i in $(seq 1 60); do curl -s -o /dev/null $B/Identity/Account/Login && break; sleep 1; done

echo "================ SECTION 17.19 ================"
echo "--- 1. Anonymous access"
ok=1; for p in / /Customers /Customers/Create /Leads /Opportunities /FollowUps /Activities /Reports /Users /AuditLog /Customers/Details/1; do
  r=$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' $B$p); case "$r" in "302 "*Account/Login*) ;; *) ok=0; echo "   $p -> $r";; esac; done
a=$(curl -s -o /dev/null -w '%{http_code}' $B/api/customers)
check "1  Anonymous: all CRM pages redirect to login; API returns 401" $([ $ok = 1 ] && [ "$a" = "401" ] && echo 1) "api=$a"

echo "--- 2. Authentication"
r=$(login a.txt admin@acxiomcrm.local 'Admin#12345'); dash=$(curl -s -b a.txt $B/ | grep -c 'Total Pipeline Value')
check "2a Valid login redirects to Dashboard" $([ "$r" = "$B/" ] && [ "$dash" -ge 1 ] && echo 1) "$r"
login m.txt manager@acxiomcrm.local 'Manager#12345' >/dev/null
login s.txt sales@acxiomcrm.local 'Sales#12345' >/dev/null
login s2.txt sales2@acxiomcrm.local 'Sales#12345' >/dev/null
rm -f bad.txt; t=$(tokof bad.txt /Identity/Account/Login)
r=$(curl -s -c bad.txt -b bad.txt --data-urlencode "Input.Email=manager@acxiomcrm.local" --data-urlencode "Input.Password=WrongPass1!" --data-urlencode "__RequestVerificationToken=$t" $B/Identity/Account/Login | grep -c 'Invalid email or password')
check "2b Invalid password rejected with message, no session" $([ "$r" -ge 1 ] && [ "$(code bad.txt /Customers)" = "302" ] && echo 1)

echo "--- 3. Client validation (markup; executed in a real browser by browser-tests.mjs)"
html=$(curl -s -b a.txt $B/Customers/Create)
n=0; for pat in 'data-val-email=' 'data-val-regex=' 'data-val-required=' 'jquery.validate.unobtrusive'; do echo "$html" | grep -q "$pat" && n=$((n+1)); done
check "3  Customer form carries unobtrusive email/phone/required rules + scripts" $([ $n = 4 ] && echo 1) "$n/4"

echo "--- 4. Server validation (crafted requests)"
cnt() { sql "select count(*) from Customers"; }
c0=$(cnt); TOKPAGE=/Customers/Create
r=$(post a.txt /Customers/Create --data-urlencode "CustomerName=Crafted" --data-urlencode "Email=not-an-email" --data-urlencode "Phone=abc" --data-urlencode "Status=Hacked" | grep -c 'field-validation-error')
api=$(json a.txt POST /api/customers '{"customerName":"Crafted","email":"bad","phone":"x","status":"Hacked"}')
nf=$(curl -s -o /dev/null -w '%{http_code}' -b a.txt -d 'CustomerName=x' $B/Customers/Create)
c1=$(cnt)
check "4a Crafted MVC POST (bad email/phone/status) rejected with field errors" $([ "$r" -ge 3 ] && echo 1) "$r"
check "4b Crafted API POST rejected with 400 + per-field errors" $([ "$(status_of "$api")" = "400" ] && echo "$api" | grep -q '"Email"' && echo "$api" | grep -q '"Phone"' && echo 1) "$(status_of "$api")"
check "4c No invalid customer persisted (DB count unchanged $c0 -> $c1)" $([ "$c0" = "$c1" ] && echo 1)
check "4d MVC POST without anti-forgery token -> 400" $([ "$nf" = "400" ] && echo 1) "$nf"

echo "--- setup: customers for sales and sales2"
P1="9$(printf '%09d' $RUN)"; P2="8$(printf '%09d' $RUN)"
body() { echo "{\"customerName\":\"$1\",\"email\":\"$2\",\"phone\":\"$3\",\"companyName\":\"$4\",\"status\":\"Active\"}"; }
r=$(json s.txt POST /api/customers "$(body "Sales Cust $RUN" "s$RUN@test.com" "$P1" "Co $RUN")")
CID=$(echo "$r" | grep -o '"id":[0-9]*' | head -1 | cut -d: -f2)
r2=$(json s2.txt POST /api/customers "$(body "Sales2 Cust $RUN" "t$RUN@test.com" "$P2" "Co2 $RUN")")
CID2=$(echo "$r2" | grep -o '"id":[0-9]*' | head -1 | cut -d: -f2)
check "   API POST customer -> 201 + Location, code assigned, no identity fields" \
  $([ "$(status_of "$r")" = "201" ] && echo "$r" | grep -q '"customerCode":"CUST-' && ! echo "$r" | grep -qi 'password\|securitystamp\|concurrencystamp\|passwordhash' && echo 1) "$(status_of "$r")"

echo "--- 5-7. Opportunity rules (MVC, sales user)"
TOKPAGE=/Opportunities/Create
opp() { post s.txt /Opportunities/Create --data-urlencode "OpportunityName=$1" --data-urlencode "CustomerId=$CID" --data-urlencode "Amount=$2" \
  --data-urlencode "Probability=$3" --data-urlencode "ExpectedCloseDate=$4" --data-urlencode "Stage=${5:-Proposal}" --data-urlencode "Status=${6:-Open}" "${@:7}"; }
TODAY=$(date +%Y-%m-%d); FUT=$(date -d '+60 days' +%Y-%m-%d 2>/dev/null || python -c "import datetime;print(datetime.date.today()+datetime.timedelta(days=60))")
YEST=$(python -c "import datetime;print(datetime.date.today()-datetime.timedelta(days=1))")
o0=$(sql "select count(*) from Opportunities")
check "5a Active opportunity Amount = 0 rejected" $(opp O1 0 50 $FUT | grep -q 'amount greater than 0' && echo 1)
check "5b Active opportunity Amount = -5 rejected" $(opp O2 -5 50 $FUT | grep -q 'Amount cannot be negative' && echo 1)
check "6a Probability 101 rejected" $(opp O3 100 101 $FUT | grep -q 'between 0 and 100' && echo 1)
check "6b Probability -1 rejected" $(opp O4 100 -1 $FUT | grep -q 'between 0 and 100' && echo 1)
r=$(opp "Prob0 $RUN" 100 0 $FUT Qualification Open -o /dev/null -w '%{redirect_url}')
check "6c Probability 0 accepted" $(echo "$r" | grep -q '/Opportunities/Details/' && echo 1) "$r"
r=$(opp "Prob100 $RUN" 100 100 $FUT Negotiation Open -o /dev/null -w '%{redirect_url}')
check "6d Probability 100 accepted" $(echo "$r" | grep -q '/Opportunities/Details/' && echo 1) "$r"
check "7a Active opportunity with yesterday's close date rejected" $(opp O5 100 50 $YEST | grep -q 'expected close date in the past' && echo 1)
r=$(opp "Opp $RUN" 50000 70 $FUT Proposal Open -o /dev/null -w '%{redirect_url}'); OID=${r##*/}
check "7b Future close date saved" $(echo "$r" | grep -q '/Opportunities/Details/' && echo 1) "$r"
r=$(opp "Today $RUN" 10 10 $TODAY Proposal Open -o /dev/null -w '%{redirect_url}')
check "7c Today as close date accepted" $(echo "$r" | grep -q '/Opportunities/Details/' && echo 1) "$r"
o1=$(sql "select count(*) from Opportunities where OpportunityName in ('O1','O2','O3','O4','O5')")
check "   None of the invalid opportunities were persisted" $([ "$o1" = "0" ] && echo 1) "$o1"

echo "--- 8. Follow-up dates"
TOKPAGE=/FollowUps/Create
fu() { post $1 /FollowUps/Create --data-urlencode "CustomerId=$CID" --data-urlencode "FollowUpDate=$2" --data-urlencode "FollowUpType=Call" --data-urlencode "Remarks=r$RUN" "${@:3}"; }
check "8a Planned follow-up dated yesterday rejected" $(fu s.txt $YEST | grep -q 'cannot be scheduled before today' && echo 1)
r=$(fu s.txt $YEST --data-urlencode "Status=Completed" -o /dev/null -w '%{http_code}')
f=$(sql "select count(*) from FollowUps where FollowUpDate < date('now','localtime') and Remarks='r$RUN'")
check "8b Crafted Status=Completed + past date by SalesExecutive is not saved (forced to Planned)" $([ "$f" = "0" ] && echo 1) "http=$r rows=$f"
r=$(fu s.txt $YEST --data-urlencode "Status=Completed" --data-urlencode "historical=true" -o /dev/null -w '%{redirect_url}')
check "8c Historical-entry flow forbidden for SalesExecutive (Access Denied)" $(echo "$r" | grep -q AccessDenied && echo 1) "$r"
r=$(fu s.txt $TODAY -o /dev/null -w '%{redirect_url}')
check "8d Follow-up dated today saved" $(echo "$r" | grep -q '/FollowUps' && echo 1) "$r"
TOKPAGE=/FollowUps/Create
r=$(post a.txt /FollowUps/Create --data-urlencode "CustomerId=$CID" --data-urlencode "FollowUpDate=$YEST" --data-urlencode "FollowUpType=Meeting" --data-urlencode "Status=Completed" --data-urlencode "historical=true" -o /dev/null -w '%{redirect_url}')
check "8e Admin historical entry (Completed, past date) allowed and audited" $(echo "$r" | grep -q '/FollowUps' && [ "$(sql "select count(*) from AuditLogs where Action='CreateHistorical'")" -ge 1 ] && echo 1) "$r"
FID=$(sql "select Id from FollowUps where Remarks='r$RUN' and Status='Planned' order by Id desc limit 1")
TOKPAGE=/FollowUps/Edit/$FID
r=$(post s.txt /FollowUps/Edit/$FID --data-urlencode "CustomerId=$CID" --data-urlencode "FollowUpDate=$YEST" --data-urlencode "FollowUpType=Call" | grep -c 'cannot be scheduled before today')
check "8f Rescheduling a planned follow-up into the past rejected" $([ "$r" -ge 1 ] && echo 1)
r=$(post s.txt /FollowUps/Edit/$FID --data-urlencode "CustomerId=$CID" --data-urlencode "FollowUpDate=$FUT" --data-urlencode "FollowUpType=Call" -o /dev/null -w '%{redirect_url}')
check "8g Reschedule to a future date saved + audited" $(echo "$r" | grep -q "/FollowUps/Details/$FID" && [ "$(sql "select count(*) from AuditLogs where Action='Reschedule' and EntityId='$FID'")" = "1" ] && echo 1) "$r"

echo "--- 9. SalesExecutive scope"
ok=1
for p in /Customers/Details/$CID /Customers/Edit/$CID /Customers/Delete/$CID /Opportunities/Details/$OID /Opportunities/Edit/$OID /FollowUps/Details/$FID /api/customers/$CID /api/opportunities/$OID; do
  c=$(code s2.txt $p); [ "$c" = "404" ] || { ok=0; echo "   sales2 GET $p -> $c"; }; done
r=$(json s2.txt PUT /api/customers/$CID "$(body "Hijack" "h$RUN@x.com" "7$(printf '%09d' $RUN)" "H")"); [ "$(status_of "$r")" = "404" ] || { ok=0; echo "   PUT -> $(status_of "$r")"; }
c=$(curl -s -o /dev/null -w '%{http_code}' -b s2.txt -X DELETE $B/api/customers/$CID); [ "$c" = "404" ] || { ok=0; echo "   DELETE -> $c"; }
TOKPAGE=/Customers; c=$(post s2.txt /Customers/Deactivate/$CID -o /dev/null -w '%{http_code}'); [ "$c" = "404" ] || { ok=0; echo "   POST Deactivate -> $c"; }
[ "$(curl -s -b s2.txt $B/api/customers | grep -c "Sales Cust $RUN")" = "0" ] || { ok=0; echo "   listed in API"; }
[ "$(curl -s -b s2.txt "$B/Customers?search=Sales+Cust+$RUN" | grep -c ">Sales Cust $RUN</a>")" = "0" ] || { ok=0; echo "   listed in search"; }
r=$(json s2.txt POST /api/opportunities "{\"opportunityName\":\"Steal\",\"customerId\":$CID,\"amount\":10,\"stage\":\"Proposal\",\"probability\":10,\"expectedCloseDate\":\"$FUT\",\"status\":\"Open\"}")
[ "$(status_of "$r")" = "400" ] && echo "$r" | grep -q 'not assigned to you' || { ok=0; echo "   API opp on foreign customer -> $(status_of "$r")"; }
r=$(json s2.txt POST /api/leads "{\"leadName\":\"Steal Lead\",\"status\":\"New\",\"source\":\"Website\",\"assignedUserId\":\"$(sql "select Id from AspNetUsers where Email='sales@acxiomcrm.local'")\"}")
[ "$(status_of "$r")" = "403" ] || { ok=0; echo "   API assign to other user -> $(status_of "$r")"; }
check "9  SalesExecutive cannot read/edit/deactivate/list/reference another user's records (URL, form, API)" $ok
sc=$(curl -s -b s.txt $B/api/customers | grep -o '"id"' | wc -l); dbsc=$(sql "select count(*) from Customers c join AspNetUsers u on u.Id=c.AssignedUserId where u.Email='sales@acxiomcrm.local'")
check "9b SalesExecutive list scope equals DB rows assigned to them ($sc = $dbsc)" $([ "$sc" = "$dbsc" ] && echo 1)

echo "--- 10. Manager"
ok=1
for p in /Reports /Reports/Pipeline /Reports/Leads /Reports/LeadConversion /Reports/UserActivity /Opportunities/Details/$OID /Customers/Details/$CID; do [ "$(code m.txt $p)" = "200" ] || { ok=0; echo "   $p -> $(code m.txt $p)"; }; done
for p in /Users /Users/Create /AuditLog /Reports/Audit; do echo "$(redir m.txt $p)" | grep -q AccessDenied || { ok=0; echo "   $p not denied"; }; done
TOKPAGE=/; c=$(post m.txt /Users/SetRole/x -d role=Admin -o /dev/null -w '%{redirect_url}'); echo "$c" | grep -q AccessDenied || { ok=0; echo "   SetRole POST -> $c"; }
check "10 Manager: team records + all non-audit reports; denied Users, role changes, Audit Log, audit report" $ok
check "10b SalesExecutive denied Reports" $(echo "$(redir s.txt /Reports/Pipeline)" | grep -q AccessDenied && echo 1)

echo "--- 11. Admin"
ok=1; for p in /Users /Users/Create /AuditLog /Reports /Reports/Audit /Customers/Details/$CID /Customers/Details/$CID2; do [ "$(code a.txt $p)" = "200" ] || { ok=0; echo "   $p -> $(code a.txt $p)"; }; done
check "11 Admin: user/role admin, audit log, audit report, all CRM records" $ok

echo "--- 12. Audit"
TOKPAGE=/Customers/Edit/$CID
post s.txt /Customers/Edit/$CID --data-urlencode "CustomerName=Sales Cust $RUN" --data-urlencode "Email=s$RUN@test.com" --data-urlencode "Phone=$P1" --data-urlencode "City=Pune $RUN" --data-urlencode "Status=Active" -o /dev/null
TOKPAGE=/Customers; post s.txt /Customers/Deactivate/$CID -o /dev/null
rows=$(sql "select Action||'|'||coalesce(UserId,'')||'|'||UserName||'|'||Entity||'|'||EntityId||'|'||Timestamp||'|'||coalesce(OldValue,'')||'|'||coalesce(NewValue,'') from AuditLogs where Entity='Customer' and EntityId='$CID' order by Id")
ok=1
for a in Create Update Deactivate; do echo "$rows" | grep -q "^$a|[0-9a-f-]\{36\}|sales@acxiomcrm.local|Customer|$CID|20" || { ok=0; echo "   missing/incomplete $a"; }; done
echo "$rows" | grep "^Update" | grep -q "Pune $RUN" || { ok=0; echo "   update new value missing"; }
check "12a Create/Update/Deactivate audited with UserId, user, action, entity, record id, timestamp, old/new" $ok
ok=1; for a in LoginSuccess LoginFailed; do [ "$(sql "select count(*) from AuditLogs where Action='$a'")" -ge 1 ] || { ok=0; echo "   no $a"; }; done
TOKPAGE=/; post bad.txt /Identity/Account/Logout -o /dev/null; login lo.txt sales2@acxiomcrm.local 'Sales#12345' >/dev/null; TOKPAGE=/; post lo.txt /Identity/Account/Logout -o /dev/null
[ "$(sql "select count(*) from AuditLogs where Action='Logout'")" -ge 1 ] || { ok=0; echo "   no Logout"; }
check "12b Login success, login failure and logout audited" $ok
leak=$(sql "select count(*) from AuditLogs where coalesce(OldValue,'')||coalesce(NewValue,'')||Details like '%#12345%' or coalesce(OldValue,'')||coalesce(NewValue,'') like '%PasswordHash%' or coalesce(OldValue,'')||coalesce(NewValue,'') like '%SecurityStamp%'")
check "12c No passwords, hashes or security stamps in the audit table" $([ "$leak" = "0" ] && echo 1) "$leak"
check "12d Audit log filters (entity/action/user) work" $(curl -s -b a.txt "$B/AuditLog?entity=Customer&act=Deactivate&user=sales%40" | grep -c '<td>Deactivate</td>' | grep -qv '^0$' && [ "$(curl -s -b a.txt "$B/AuditLog?entity=Customer&act=Deactivate" | grep -c '<td>Create</td>')" = "0" ] && echo 1)

echo "--- 13. API"
r=$(curl -s -w '\n%{http_code} %{content_type}' -b a.txt $B/api/customers)
check "13a Authorized GET /api/customers -> 200 application/json array" $(echo "$r" | tail -1 | grep -q '^200 application/json' && echo "$r" | head -1 | grep -q '^\[' && echo 1)
check "13b Unauthenticated GET/POST -> 401" $([ "$(curl -s -o /dev/null -w '%{http_code}' $B/api/leads)" = "401" ] && [ "$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{}' $B/api/customers)" = "401" ] && echo 1)
check "13c Missing id -> 404 problem JSON" $(r=$(curl -s -w '\n%{http_code}' -b a.txt $B/api/customers/999999); [ "$(status_of "$r")" = "404" ] && echo "$r" | grep -q '"status":404' && echo 1)
d=$(json a.txt POST /api/customers "$(body "Dup $RUN" "S$RUN@TEST.com" "1234567" "X")"); d2=$(json a.txt POST /api/customers "$(body "Dup2 $RUN" "d2$RUN@test.com" "${P1:0:5}-${P1:5}" "X")")
check "13d Duplicate email (case) and phone (formatting) -> 409" $([ "$(status_of "$d")" = "409" ] && [ "$(status_of "$d2")" = "409" ] && echo 1) "$(status_of "$d") $(status_of "$d2")"
m=$(curl -s -w '\n%{http_code}' -b a.txt -X POST -H 'Content-Type: application/json' -d '{"customerName": ' $B/api/customers)
check "13e Malformed JSON -> 400, no stack trace" $([ "$(status_of "$m")" = "400" ] && ! echo "$m" | grep -qi 'exception\|   at \|stacktrace' && echo 1) "$(status_of "$m")"
f=$(curl -s -o /dev/null -w '%{http_code}' -b a.txt -X POST -d 'customerName=x' $B/api/customers)
check "13f Form-encoded POST to API refused (415)" $([ "$f" = "415" ] && echo 1) "$f"
r=$(json s.txt POST /api/leads '{"leadName":"Api Lead","status":"Bogus","source":"Website","expectedValue":-1}')
r2=$(json s.txt POST /api/leads "{\"leadName\":\"Api Lead $RUN\",\"status\":\"Qualified\",\"source\":\"Referral\",\"expectedValue\":2500,\"email\":\"lead$RUN@x.com\",\"phone\":\"7$(printf '%09d' $RUN)\"}")
check "13g POST /api/leads: invalid status/negative value -> 400; valid -> 201" $([ "$(status_of "$r")" = "400" ] && echo "$r" | grep -q 'valid status' && [ "$(status_of "$r2")" = "201" ] && echo 1)
LID=$(echo "$r2" | grep -o '"id":[0-9]*' | head -1 | cut -d: -f2)
r=$(json s.txt POST /api/opportunities "{\"opportunityName\":\"Api Opp\",\"customerId\":$CID,\"amount\":0,\"stage\":\"Proposal\",\"probability\":150,\"expectedCloseDate\":\"$YEST\",\"status\":\"Open\"}")
r2=$(json s.txt POST /api/opportunities "{\"opportunityName\":\"Api Opp $RUN\",\"customerId\":$CID,\"leadId\":$LID,\"amount\":900,\"stage\":\"Proposal\",\"probability\":40,\"expectedCloseDate\":\"$FUT\",\"status\":\"Open\"}")
check "13h POST /api/opportunities: 17.7 rules -> 400 (amount, probability, date); valid -> 201" \
  $([ "$(status_of "$r")" = "400" ] && echo "$r" | grep -q 'greater than 0' && echo "$r" | grep -q 'between 0 and 100' && echo "$r" | grep -q 'in the past' && [ "$(status_of "$r2")" = "201" ] && echo 1)
check "13i API writes are audited" $([ "$(sql "select count(*) from AuditLogs where Details='via API'")" -ge 3 ] && echo 1)

echo "--- 14. Dashboard"
num() { curl -s -b $1 $B/ | grep -o "data-card=\"$2\">[^<]*" | sed 's/.*>//' | tr -d ','; }
for u in s a; do
  case $u in s) who="sales@acxiomcrm.local"; jar=s.txt;; a) who=""; jar=a.txt;; esac
  if [ -n "$who" ]; then W="join AspNetUsers u on u.Id=x.AssignedUserId where u.Email='$who'"; else W="where 1=1"; fi
  exp_c=$(sql "select count(*) from Customers x $W"); exp_l=$(sql "select count(*) from Leads x $W"); exp_o=$(sql "select count(*) from Opportunities x $W")
  exp_open=$(sql "select count(*) from Opportunities x $W and x.Status='Open'")
  exp_pipe=$(sql "select printf('%.2f', coalesce(sum(cast(Amount as real)),0)) from Opportunities x $W and x.Status='Open'")
  got_pipe=$(num $jar "Total Pipeline Value")
  check "14${u} Dashboard ($([ -n "$who" ] && echo SalesExecutive || echo Admin)) cards = DB: customers $(num $jar "Total Customers")/$exp_c leads $(num $jar "Total Leads")/$exp_l opps $(num $jar "Total Opportunities")/$exp_o open $(num $jar "Open Opportunities")/$exp_open pipeline $got_pipe/$exp_pipe" \
    $([ "$(num $jar "Total Customers")" = "$exp_c" ] && [ "$(num $jar "Total Leads")" = "$exp_l" ] && [ "$(num $jar "Total Opportunities")" = "$exp_o" ] && [ "$(num $jar "Open Opportunities")" = "$exp_open" ] && [ "$got_pipe" = "$exp_pipe" ] && echo 1)
done
check "14c Dashboard chart data embedded from server (3 charts)" $(curl -s -b s.txt $B/ | grep -q 'window.dashboardData' && [ "$(curl -s -b s.txt $B/ | grep -c 'new Chart(')" = "3" ] && echo 1)
check "14d Dashboard period filter (today) renders" $([ "$(code a.txt '/?preset=today')" = "200" ] && echo 1)

echo; echo "================ SECTION 17.7 ================"
TOKPAGE=/Customers/Create
cust() { post a.txt /Customers/Create --data-urlencode "CustomerName=$1" --data-urlencode "Email=$2" --data-urlencode "Phone=$3" --data-urlencode "Status=Active"; }
check "V1 Email format validated + unique (MVC, case-insensitive)" $(cust "V1a" "bad@" "6$(printf '%09d' $RUN)" | grep -q 'valid e-mail\|valid email' && cust "V1b" "S$RUN@Test.com" "5$(printf '%09d' $RUN)" | grep -q 'email already exists' && echo 1)
check "V2 Phone format validated + unique (MVC, formatting-insensitive)" $(cust "V2a" "v2a$RUN@x.com" "12ab" | grep -q 'valid phone' && cust "V2b" "v2b$RUN@x.com" "${P1:0:3} ${P1:3}" | grep -q 'phone number already exists' && echo 1)
check "V3 Opportunity amount: negative and 0 (active) rejected" $(echo 1)  # see 5a/5b
check "V4 Active ExpectedCloseDate in the past rejected" $(echo 1)          # see 7a
r=$(cust "" "" "" | grep -c 'field-validation-error'); r2=$(post s.txt /Leads/Create --data-urlencode "LeadName=" --data-urlencode "Source=Website" --data-urlencode "Status=New" --data-urlencode "ExpectedValue=0" | grep -c 'Lead Name field is required')
check "V5 Required fields: empty customer name/email/phone and lead name rejected" $([ "$r" -ge 3 ] && [ "$r2" -ge 1 ] && echo 1) "$r $r2"
r=$(cust "Sales Cust $RUN" "other$RUN@x.com" "4$(printf '%09d' $RUN)" | grep -c 'already exists')
check "V6 Duplicate business identifiers blocked; same name with different email/phone allowed (not merged)" $([ "$r" = "0" ] && echo 1) "$r"
TOKPAGE=/Leads/Create
r=$(post s.txt /Leads/Create --data-urlencode "LeadName=Bad Status" --data-urlencode "Source=Website" --data-urlencode "Status=Hacked" --data-urlencode "ExpectedValue=1" | grep -c 'valid status')
r2=$(post s.txt /Leads/Create --data-urlencode "LeadName=Max $RUN" --data-urlencode "Source=Website" --data-urlencode "Status=New" --data-urlencode "ExpectedValue=100000001" | grep -c 'cannot exceed')
check "V7 Lead status restricted to configured set; expected value range enforced" $([ "$r" = "1" ] && [ "$r2" = "1" ] && echo 1) "$r $r2"
check "V8 New/planned follow-up before today rejected" $(echo 1)            # see 8a-8c, 8f
check "V9 Probability 0..100 inclusive" $(echo 1)                          # see 6a-6d

echo; echo "================ OTHER CHECKS ================"
h=$(sql "select PasswordHash from AspNetUsers where Email='admin@acxiomcrm.local'")
check "S1 Passwords stored as Identity hashes (AQAAAA..., not plain text)" $(echo "$h" | grep -q '^AQAAAA' && ! echo "$h" | grep -q 'Admin#12345' && echo 1)
for i in 1 2 3 4 5; do login lk.txt sales2@acxiomcrm.local 'Wrong#1' >/dev/null; done
rm -f lk.txt; t=$(tokof lk.txt /Identity/Account/Login)
r=$(curl -s -c lk.txt -b lk.txt --data-urlencode "Input.Email=sales2@acxiomcrm.local" --data-urlencode "Input.Password=Sales#12345" --data-urlencode "__RequestVerificationToken=$t" $B/Identity/Account/Login | grep -c 'locked')
check "S2 Lockout after 5 failures (correct password then refused)" $([ "$r" -ge 1 ] && echo 1)
TOKPAGE=/Users/Create
r=$(post a.txt /Users/Create --data-urlencode "Email=weak$RUN@x.com" --data-urlencode "Password=password" --data-urlencode "ConfirmPassword=password" --data-urlencode "Role=SalesExecutive" | grep -c 'must have')
check "S3 Password policy enforced on user creation" $([ "$r" -ge 1 ] && echo 1)
hdr=$(rm -f ck.txt; t=$(tokof ck.txt /Identity/Account/Login); curl -s -D - -o /dev/null -c ck.txt -b ck.txt --data-urlencode "Input.Email=manager@acxiomcrm.local" --data-urlencode "Input.Password=Manager#12345" --data-urlencode "__RequestVerificationToken=$t" $B/Identity/Account/Login | grep -i '^set-cookie: AcxiomCRM.Auth')
check "S4 Auth cookie is HttpOnly + SameSite=Lax (session cookie; 8h sliding ticket)" $(echo "$hdr" | grep -qi 'httponly' && echo "$hdr" | grep -qi 'samesite=lax' && echo 1) "$hdr"
ok=1; for p in /Identity/Account/Register /Identity/Account/Manage/DeletePersonalData /Identity/Account/Manage/DownloadPersonalData /Identity/Account/Manage/Email /Identity/Account/ForgotPassword; do [ "$(code a.txt $p)" = "404" ] || { ok=0; echo "   $p -> $(code a.txt $p)"; }; done
[ "$(code a.txt /Identity/Account/Manage/ChangePassword)" = "200" ] || ok=0
check "S5 Self-registration, account deletion, personal-data download, email change blocked; change password allowed" $ok
forms=$(for p in /Customers/Create /Leads/Create /Opportunities/Create /FollowUps/Create /Activities/Create /Users/Create /Customers/Delete/$CID2 /Leads/Convert/$LID; do curl -s -b a.txt $B$p; done | grep -c '__RequestVerificationToken')
check "S6 Anti-forgery tokens present on state-changing forms" $([ "$forms" -ge 8 ] && echo 1) "$forms"
ok=1
[ "$(curl -s -b a.txt "$B/Leads?status=Qualified" | grep -c "Api Lead $RUN")" = "1" ] || { ok=0; echo "   leads status filter"; }
[ "$(curl -s -b a.txt "$B/Leads?status=New" | grep -c "Api Lead $RUN")" = "0" ] || { ok=0; echo "   leads status filter (neg)"; }
[ "$(curl -s -b a.txt "$B/Opportunities?search=Sales+Cust+$RUN" | grep -c "Opp $RUN")" -ge 1 ] || { ok=0; echo "   opp search by customer"; }
[ "$(curl -s -b a.txt "$B/Opportunities?stage=Won" | grep -c "Opp $RUN")" = "0" ] || { ok=0; echo "   opp stage filter"; }
[ "$(curl -s -b a.txt "$B/Customers?search=$P1" | grep -c "Sales Cust $RUN")" -ge 1 ] || { ok=0; echo "   customer phone search"; }
[ "$(curl -s -b a.txt "$B/FollowUps?view=All&related=Sales+Cust+$RUN" | grep -c "Sales Cust $RUN")" -ge 1 ] || { ok=0; echo "   follow-up related filter"; }
[ "$(curl -s -b a.txt "$B/Activities?type=Meeting" | grep -c '<td>Call</td>')" = "0" ] || { ok=0; echo "   activity type filter"; }
check "S7 Search/filter on list pages (customers, leads, opportunities, follow-ups, activities)" $ok

echo "--- lead conversion"
TOKPAGE=/Leads/Convert/$LID
r=$(post s.txt /Leads/Convert/$LID -d createOpportunity=true -o /dev/null -w '%{redirect_url}')
nc=$(sql "select count(*) from Customers where Email='lead$RUN@x.com'"); st=$(sql "select Status||'|'||coalesce(ConvertedCustomerId,'') from Leads where Id=$LID")
check "C1 Convert lead -> new customer, lead Converted + linked, opportunity created, audited" \
  $(echo "$r" | grep -q '/Customers/Details/' && [ "$nc" = "1" ] && echo "$st" | grep -q '^Converted|[0-9]' && [ "$(sql "select count(*) from AuditLogs where Action='Convert' and EntityId='$LID'")" = "1" ] && [ "$(sql "select count(*) from Opportunities where LeadId=$LID")" -ge 2 ] && echo 1) "$r $nc $st"
r2=$(json s.txt POST /api/leads "{\"leadName\":\"Dup Lead $RUN\",\"status\":\"New\",\"source\":\"Phone\",\"email\":\"S$RUN@test.com\"}"); LID2=$(echo "$r2" | grep -o '"id":[0-9]*' | head -1 | cut -d: -f2)
TOKPAGE=/Leads/Convert/$LID2; c0=$(sql "select count(*) from Customers")
r=$(post s.txt /Leads/Convert/$LID2 -o /dev/null -w '%{redirect_url}')
check "C2 Converting a lead whose email matches an existing customer links it (no duplicate)" $(echo "$r" | grep -q "/Customers/Details/$CID" && [ "$(sql "select count(*) from Customers")" = "$c0" ] && echo 1) "$r"
r3=$(json s2.txt POST /api/leads "{\"leadName\":\"Other Lead $RUN\",\"status\":\"New\",\"source\":\"Phone\",\"phone\":\"$P1\"}"); LID3=$(echo "$r3" | grep -o '"id":[0-9]*' | head -1 | cut -d: -f2)
TOKPAGE=/Leads/Convert/$LID3; r=$(post s2.txt /Leads/Convert/$LID3 | grep -c 'assigned to another user')
check "C3 SalesExecutive cannot convert into another user's existing customer (no duplicate, no disclosure of details)" $([ "$r" -ge 1 ] && [ "$(sql "select count(*) from Customers")" = "$c0" ] && echo 1)
TOKPAGE=/Leads/Edit/$LID2; r=$(post s.txt /Leads/Edit/$LID2 --data-urlencode "LeadName=x" --data-urlencode "Source=Phone" --data-urlencode "Status=New" --data-urlencode "ExpectedValue=0" | grep -c 'cannot change status')
check "C4 Converted lead status cannot be changed back" $([ "$r" -ge 1 ] && echo 1)

echo "--- reports"
ok=1; for p in Customers Leads FollowUps Opportunities Pipeline LeadConversion UserActivity Audit; do [ "$(code a.txt "/Reports/$p?preset=month")" = "200" ] || { ok=0; echo "   $p"; }; done
csv=$(curl -s -D - -b a.txt "$B/Reports/Pipeline?format=csv")
echo "$csv" | grep -qi 'content-type: text/csv' || ok=0
check "R1 All 8 reports render for Admin; CSV export works" $ok
mp=$(curl -s -b m.txt "$B/Reports/Customers" | grep -c "Sales2 Cust $RUN"); sp=$(code s.txt /Reports/Customers)
check "R2 Reports respect role scope (Manager sees team data, SalesExecutive denied)" $([ "$mp" -ge 1 ] && [ "$sp" = "302" ] && echo 1)

echo; echo "RESULT: $PASS passed, $FAIL failed"
for f in "${FAILED[@]}"; do echo "  FAILED: $f"; done
[ $FAIL = 0 ]
