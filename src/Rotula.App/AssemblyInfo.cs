using System.Windows;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// The UI text lives in satellite assemblies (Strings.en / es / pt-BR / fr-CA .resx) and Loc always asks for one of those
// cultures explicitly, so no neutral-language attribute is used: NeutralResourcesLanguage(..., Satellite) would also redirect WPF's
// own XAML resource lookup to a satellite and the application would fail to start.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Rotula.Tests")]
