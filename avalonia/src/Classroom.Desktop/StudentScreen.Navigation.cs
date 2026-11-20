using Avalonia.Controls;

namespace Classroom.Desktop;

public sealed partial class StudentScreen
{
    readonly Grid navigationContainer;
    readonly Control navigationBody,navigationSummary;
    readonly Action refreshNavigationLayout;
    bool navigationHosted;
    internal bool HasLoaded=>catalogue is not null;
    internal (Control Body,Control Summary) TakeNavigation()
    {
        if(!navigationHosted)
        {
            navigationContainer.Children.Remove(navigationBody);navigationContainer.Children.Remove(navigationSummary);
            navigationHosted=true;refreshNavigationLayout();
        }
        return(navigationBody,navigationSummary);
    }
    internal void RestoreNavigation()
    {
        if(!navigationHosted)return;
        navigationContainer.Children.Add(navigationBody);navigationContainer.Children.Add(navigationSummary);
        navigationHosted=false;refreshNavigationLayout();
    }
    internal Task OpenGlossary(Control? trigger)=>Glossary(trigger);
}
