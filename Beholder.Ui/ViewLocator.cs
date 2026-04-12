using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Beholder.Ui.ViewModels;

namespace Beholder.Ui;

[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate {
    public Control? Build(object? param) {
        if (param is null)
            return null;

        var vmName = param.GetType().FullName!;
        var viewName = vmName.Replace("ViewModel", "View", StringComparison.Ordinal);

        var type = Type.GetType(viewName);
        if (type is null) {
            // Tab ViewModels live in ViewModels/ but tab Views live in Views/Tabs/
            var tabViewName = viewName.Replace(".Views.", ".Views.Tabs.", StringComparison.Ordinal);
            type = Type.GetType(tabViewName);
        }

        if (type is not null)
            return (Control)Activator.CreateInstance(type)!;

        return new TextBlock { Text = "Not Found: " + viewName };
    }

    public bool Match(object? data) {
        return data is ViewModelBase;
    }
}
