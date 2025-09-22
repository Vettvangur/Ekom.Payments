using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace Ekom.Payments.Services;

public interface IRenderViewService
{
    Task<string> RenderView(string viewName, object model);
}

public class RenderViewService : IRenderViewService
{
    private readonly IRazorViewEngine _razorViewEngine;
    private readonly ITempDataProvider _tempDataProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public RenderViewService(
        IRazorViewEngine razorViewEngine,
        ITempDataProvider tempDataProvider,
        IServiceProvider serviceProvider,
        IHttpContextAccessor httpContextAccessor)
    {
        _razorViewEngine = razorViewEngine;
        _tempDataProvider = tempDataProvider;
        _serviceProvider = serviceProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<string> RenderView(string viewName, object model)
    {
        var httpContext = _httpContextAccessor.HttpContext
        ?? new DefaultHttpContext { RequestServices = _serviceProvider };

        return await RenderView(viewName, model, new Dictionary<string, dynamic>(), httpContext);
    }

    public async Task<string> RenderView(string viewName, object model, Dictionary<string, dynamic> viewData, HttpContext httpContext)
    {

        var routeData = httpContext.GetRouteData() ?? new RouteData();
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        using var sw = new StringWriter();

        var viewResult = _razorViewEngine.GetView(executingFilePath: null, viewPath: viewName, isMainPage: false);
        if (!viewResult.Success || viewResult.View == null)
        {
            throw new InvalidOperationException($"View '{viewName}' not found.");
        }

        var viewDictionary = new ViewDataDictionary(
            new EmptyModelMetadataProvider(),
            new ModelStateDictionary())
        {
            Model = model
        };

        if (viewData != null)
        {
            foreach (var entry in viewData)
            {
                viewDictionary[entry.Key] = entry.Value;
            }
        }

        var viewContext = new ViewContext(
            actionContext,
            viewResult.View,
            viewDictionary,
            new TempDataDictionary(httpContext, _tempDataProvider),
            sw,
            new HtmlHelperOptions());

        await viewResult.View.RenderAsync(viewContext);

        return sw.ToString();
    }
}
