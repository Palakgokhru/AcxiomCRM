using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AcxiomCRM.Controllers;

/// <summary>
/// Navigation placeholders for the CRM modules. None of these features are built yet --
/// each action renders the same "not implemented" page so the navigation is complete and
/// honest. Modules get their own controllers as they are actually implemented.
/// </summary>
[Authorize]
public class ModulesController : Controller
{
    [Authorize(Roles = AcxiomCRM.Data.Roles.AdminOrManager)]
    public IActionResult Reports() =>
        Placeholder("Reports", "Pipeline, conversion, and activity reporting.");

    private IActionResult Placeholder(string title, string description)
    {
        ViewData["Title"] = title;
        ViewData["Description"] = description;
        return View("Placeholder");
    }
}
