#!/usr/bin/env bash
# Acceptance checks for AcxiomCRM against a running instance (http://localhost:5058).
cd "$(dirname "$0")"
B=http://localhost:5058
RUN=$RANDOM
PASS=0; FAIL=0
check() { if [ "$2" = "1" ]; then echo "PASS  $1"; PASS=$((PASS+1)); else echo "FAIL  $1  ($3)"; FAIL=$((FAIL+1)); fi; }
tokof() { curl -s -c $1 -b $1 "$B$2" | grep -o 'name="__RequestVerificationToken" type="hidden" value="[^"]*' | head -1 | sed 's/.*value="//'; }
login() { rm -f $1; local t; t=$(tokof $1 /Identity/Account/Login)
  curl -s -o /dev/null -w '%{redirect_url}' -c $1 -b $1 --data-urlencode "Input.Email=$2" --data-urlencode "Input.Password=$3" --data-urlencode "__RequestVerificationToken=$t" $B/Identity/Account/Login; }
code() { curl -s -o /dev/null -w '%{http_code}' -b $1 "$B$2"; }
for i in $(seq 1 40); do curl -s -o /dev/null $B/Identity/Account/Login && break; sleep 1; done

echo "== 1. Anonymous access"
ok=1; for p in / /Customers /Leads /Opportunities /FollowUps /Activities /Users /AuditLog /Modules/Reports; do
  r=$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' $B$p); case "$r" in "302 "*Account/Login*) ;; *) ok=0; echo "   $p -> $r";; esac; done
check "1  Anonymous users redirected to login on all CRM pages" $ok

echo "== 2. Login"
r=$(login a.txt admin@acxiomcrm.local 'Admin#12345'); dash=$(curl -s -b a.txt $B/ | grep -c 'Total Pipeline Value')
check "2a Valid login redirects to Dashboard" $([ "$r" = "$B/" ] && [ "$dash" -ge 1 ] && echo 1) "$r"
login m.txt manager@acxiomcrm.local 'Manager#12345' >/dev/null
login s.txt sales@acxiomcrm.local 'Sales#12345' >/dev/null
login s2.txt sales2@acxiomcrm.local 'Sales#12345' >/dev/null
rm -f bad.txt; t=$(tokof bad.txt /Identity/Account/Login)
r=$(curl -s -c bad.txt -b bad.txt --data-urlencode "Input.Email=manager@acxiomcrm.local" --data-urlencode "Input.Password=WrongPass1!" --data-urlencode "__RequestVerificationToken=$t" $B/Identity/Account/Login | grep -c 'Invalid email or password')
check "2b Invalid login rejected with message" $([ "$r" -ge 1 ] && [ "$(code bad.txt /Customers)" = "302" ] && echo 1)

echo "== 3. Client validation markup"
html=$(curl -s -b a.txt $B/Customers/Create)
ok=$(echo "$html" | grep -c 'data-val-email=\|data-val-regex=\|data-val-required=\|jquery.validate.unobtrusive')
check "3  Customer form has unobtrusive email/phone/required rules + scripts (markup verified)" $([ "$ok" -ge 4 ] && echo 1) "$ok"

echo "== 4. Crafted invalid POST / API rejected"
t=$(tokof a.txt /Customers/Create)
n0=$(curl -s -b a.txt $B/api/customers | grep -o '"id"' | wc -l)
r=$(curl -s -b a.txt -c a.txt --data-urlencode "CustomerName=" --data-urlencode "Email=not-an-email" --data-urlencode "Phone=abc" --data-urlencode "Status=Hacked" --data-urlencode "__RequestVerificationToken=$t" $B/Customers/Create | grep -c 'field-validation-error')
nf=$(curl -s -o /dev/null -w '%{http_code}' -b a.txt -d 'CustomerName=x' $B/Customers/Create)
api=$(curl -s -o /dev/null -w '%{http_code}' -b a.txt -H 'Content-Type: application/json' -d '{"customerName":"","email":"bad","phone":"x","status":"Hacked"}' $B/api/customers)
n1=$(curl -s -b a.txt $B/api/customers | grep -o '"id"' | wc -l)
check "4a Crafted MVC POST rejected with field errors" $([ "$r" -ge 4 ] && echo 1) "$r"
check "4b POST without anti-forgery token -> 400" $([ "$nf" = "400" ] && echo 1) "$nf"
check "4c Invalid API payload -> 400, nothing saved" $([ "$api" = "400" ] && [ "$n0" = "$n1" ] && echo 1) "$api $n0/$n1"

echo "== Customers: uniqueness / duplicates via API"
body() { echo "{\"customerName\":\"$1\",\"email\":\"$2\",\"phone\":\"$3\",\"companyName\":\"$4\",\"address\":\"1 Test St\",\"city\":\"Pune\",\"state\":\"MH\",\"status\":\"Active\"}"; }
P="9$(printf '%09d' $RUN)"
r=$(curl -s -w ' %{http_code}' -b s.txt -H 'Content-Type: application/json' -d "$(body "Api Cust $RUN" "api$RUN@test.com" "$P" "ApiCo $RUN")" $B/api/customers)
NEWID=$(echo "$r" | grep -o '"id":[0-9]*' | head -1 | cut -d: -f2)
check "API POST valid customer -> 201 with JSON (no password fields)" $(echo "$r" | grep -q ' 201$' && ! echo "$r" | grep -qi 'password\|securitystamp' && echo 1) "$r"
d1=$(curl -s -o /dev/null -w '%{http_code}' -b a.txt -H 'Content-Type: application/json' -d "$(body "Other $RUN" "API$RUN@test.com" "1234567" "Other")" $B/api/customers)
d2=$(curl -s -o /dev/null -w '%{http_code}' -b a.txt -H 'Content-Type: application/json' -d "$(body "Other2 $RUN" "o$RUN@test.com" "${P:0:5}-${P:5}" "Other")" $B/api/customers)
d3=$(curl -s -o /dev/null -w '%{http_code}' -b a.txt -H 'Content-Type: application/json' -d "$(body "api cust $RUN" "p$RUN@test.com" "8$(printf '%09d' $RUN)" "APICO $RUN")" $B/api/customers)
check "Duplicate email (case-insensitive) -> 409" $([ "$d1" = "409" ] && echo 1) "$d1"
check "Duplicate phone (different formatting) -> 409" $([ "$d2" = "409" ] && echo 1) "$d2"
check "Duplicate name+company -> 409" $([ "$d3" = "409" ] && echo 1) "$d3"
t=$(tokof a.txt /Customers/Create)
r=$(curl -s -b a.txt -c a.txt --data-urlencode "CustomerName=Dup $RUN" --data-urlencode "Email=api$RUN@test.com" --data-urlencode "Phone=$P" --data-urlencode "CompanyName=X" --data-urlencode "Address=a" --data-urlencode "City=b" --data-urlencode "State=c" --data-urlencode "Status=Active" --data-urlencode "__RequestVerificationToken=$t" $B/Customers/Create | grep -c 'already exists')
check "Duplicate email+phone via MVC form shows messages" $([ "$r" -ge 2 ] && echo 1) "$r"

echo "== Leads"
t=$(tokof s.txt /Leads/Create)
r=$(curl -s -b s.txt -c s.txt --data-urlencode "LeadName=" --data-urlencode "Source=Website" --data-urlencode "Status=Bogus" --data-urlencode "ExpectedValue=-1" --data-urlencode "__RequestVerificationToken=$t" $B/Leads/Create | grep -c 'Lead Name field is required\|valid status\|cannot be negative')
check "Lead: required name, invalid status, negative value rejected" $([ "$r" -ge 3 ] && echo 1) "$r"
r=$(curl -s -o /dev/null -w '%{redirect_url}' -b s.txt -c s.txt --data-urlencode "LeadName=Lead $RUN" --data-urlencode "Source=Website" --data-urlencode "Status=Qualified" --data-urlencode "ExpectedValue=1000" --data-urlencode "__RequestVerificationToken=$t" $B/Leads/Create)
LEADID=${r##*/}
check "Lead: valid lead saved" $(echo "$r" | grep -q '/Leads/Details/' && echo 1) "$r"

echo "== 5-7. Opportunity rules"
t=$(tokof s.txt /Opportunities/Create)
opp() { curl -s -b s.txt -c s.txt --data-urlencode "OpportunityName=$1" --data-urlencode "CustomerId=$NEWID" --data-urlencode "Amount=$2" --data-urlencode "Probability=$3" --data-urlencode "ExpectedCloseDate=$4" --data-urlencode "Stage=Proposal" --data-urlencode "Status=Open" --data-urlencode "__RequestVerificationToken=$t" $B/Opportunities/Create "${@:5}"; }
check "5a Active opp Amount = 0 rejected" $(opp O1 0 50 2031-01-01 | grep -q 'amount greater than 0' && echo 1)
check "5b Active opp Amount < 0 rejected" $(opp O2 -5 50 2031-01-01 | grep -q 'Amount cannot be negative' && echo 1)
check "6a Probability -1 rejected" $(opp O3 100 -1 2031-01-01 | grep -q 'between 0 and 100' && echo 1)
check "6b Probability 101 rejected" $(opp O4 100 101 2031-01-01 | grep -q 'between 0 and 100' && echo 1)
check "7  Past close date on active opp rejected" $(opp O5 100 50 2020-01-01 | grep -q 'expected close date in the past' && echo 1)
r=$(opp "Opp $RUN" 50000 70 2031-01-01 -o /dev/null -w '%{redirect_url}'); OPPID=${r##*/}
check "   Valid opportunity saved" $(echo "$r" | grep -q '/Opportunities/Details/' && echo 1) "$r"

echo "== 8. Follow-up date"
t=$(tokof s.txt /FollowUps/Create)
r=$(curl -s -b s.txt -c s.txt --data-urlencode "CustomerId=$NEWID" --data-urlencode "FollowUpDate=2020-01-01" --data-urlencode "FollowUpType=Call" --data-urlencode "Status=Planned" --data-urlencode "__RequestVerificationToken=$t" $B/FollowUps/Create | grep -c 'cannot be scheduled before today')
check "8  Planned follow-up in the past rejected" $([ "$r" -ge 1 ] && echo 1)

echo "== 9. Ownership (sales2 vs sales's records)"
ok=1
for p in /Customers/Details/$NEWID /Customers/Edit/$NEWID /Leads/Details/$LEADID /Leads/Edit/$LEADID /Opportunities/Details/$OPPID /Opportunities/Edit/$OPPID /api/customers/$NEWID; do
  c=$(code s2.txt $p); [ "$c" = "404" ] || { ok=0; echo "   $p -> $c"; }; done
c=$(curl -s -o /dev/null -w '%{http_code}' -b s2.txt -X PUT -H 'Content-Type: application/json' -d "$(body "Hijack" "h$RUN@x.com" "7$(printf '%09d' $RUN)" "H")" $B/api/customers/$NEWID); [ "$c" = "404" ] || { ok=0; echo "   PUT -> $c"; }
c=$(curl -s -o /dev/null -w '%{http_code}' -b s2.txt -X DELETE $B/api/customers/$NEWID); [ "$c" = "404" ] || { ok=0; echo "   DELETE -> $c"; }
inlist=$(curl -s -b s2.txt $B/api/customers | grep -c "Api Cust $RUN"); [ "$inlist" = "0" ] || ok=0
t2=$(tokof s2.txt /Opportunities/Create)
r=$(curl -s -b s2.txt -c s2.txt --data-urlencode "OpportunityName=Steal" --data-urlencode "CustomerId=$NEWID" --data-urlencode "Amount=10" --data-urlencode "Probability=10" --data-urlencode "ExpectedCloseDate=2031-01-01" --data-urlencode "Stage=Proposal" --data-urlencode "Status=Open" --data-urlencode "__RequestVerificationToken=$t2" $B/Opportunities/Create | grep -c 'not assigned to you'); [ "$r" -ge 1 ] || ok=0
check "9  SalesExecutive blocked from another user's records (URL, edit, API GET/PUT/DELETE, crafted reference)" $ok

echo "== 10. Manager"
ok=1
[ "$(code m.txt /Modules/Reports)" = "200" ] || ok=0
[ "$(curl -s -b m.txt $B/api/customers | grep -c "Api Cust $RUN")" -ge 1 ] || ok=0   # team record visible
[ "$(code m.txt /Opportunities/Details/$OPPID)" = "200" ] || ok=0
for p in /Users /Users/Create /AuditLog; do r=$(curl -s -o /dev/null -w '%{redirect_url}' -b m.txt $B$p); echo "$r" | grep -q AccessDenied || { ok=0; echo "   $p -> $r"; }; done
check "10 Manager sees team records + Reports, denied Users/AuditLog" $ok

echo "== 11. Admin"
ok=1; for p in /Users /Users/Create /AuditLog /Modules/Reports; do [ "$(code a.txt $p)" = "200" ] || ok=0; done
check "11 Admin can open Users, Create User, Audit Log, Reports" $ok

echo "== 12. Audit"
t=$(tokof s.txt /Customers/Edit/$NEWID)
curl -s -o /dev/null -b s.txt -c s.txt --data-urlencode "CustomerName=Api Cust $RUN" --data-urlencode "Email=api$RUN@test.com" --data-urlencode "Phone=$P" --data-urlencode "CompanyName=ApiCo $RUN" --data-urlencode "Address=2 New St" --data-urlencode "City=Pune" --data-urlencode "State=MH" --data-urlencode "Status=Active" --data-urlencode "__RequestVerificationToken=$t" $B/Customers/Edit/$NEWID
curl -s -o /dev/null -b s.txt -X DELETE $B/api/customers/$NEWID
audit=$(curl -s -b a.txt $B/AuditLog)
ok=1
for pat in "Create</td>" "Update</td>" "Deactivate</td>" "LoginSuccess" "LoginFailed" "2 New St" "1 Test St"; do echo "$audit" | grep -q "$pat" || { ok=0; echo "   missing $pat"; }; done
echo "$audit" | grep -qi 'Wrong\|Sales#12345\|Admin#12345\|PasswordHash' && { ok=0; echo "   secret leaked"; }
check "12 Audit has create/update/deactivate (old+new values), login success/fail, no passwords" $ok

echo "== 13. API auth"
r=$(curl -s -o /dev/null -w '%{http_code}' $B/api/customers)
j=$(curl -s -o /dev/null -w '%{content_type}' -b a.txt $B/api/customers)
l=$(code a.txt /api/leads); o=$(code a.txt /api/opportunities)
check "13 Anonymous GET /api/customers -> 401; authorized -> JSON; /api/leads + /api/opportunities 200" $([ "$r" = "401" ] && echo "$j" | grep -q json && [ "$l$o" = "200200" ] && echo 1) "$r $j $l $o"

echo "== 14. Dashboard"
num() { curl -s -b $1 $B/ | grep -o "data-card=\"$2\">[^<]*" | sed 's/.*>//' | tr -d ','; }
sc=$(num s.txt "Total Customers"); sc2=$(num s2.txt "Total Customers"); ac=$(num a.txt "Total Customers")
apiS=$(curl -s -b s.txt $B/api/customers | grep -o '"id"' | wc -l); apiA=$(curl -s -b a.txt $B/api/customers | grep -o '"id"' | wc -l)
chart=$(curl -s -b s.txt $B/ | grep -c 'new Chart\|chart.umd.js')
check "14 Dashboard cards match scoped DB counts (sales=$sc api=$apiS, admin=$ac api=$apiA, sales2=$sc2) + chart script" \
  $([ "$sc" = "$apiS" ] && [ "$ac" = "$apiA" ] && [ "$sc2" -lt "$ac" ] && [ "$chart" -ge 2 ] && echo 1)

echo; echo "RESULT: $PASS passed, $FAIL failed"
