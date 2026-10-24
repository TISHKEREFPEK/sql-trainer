using AvaloniaEdit.Highlighting;
namespace Classroom.Desktop;

static class CodeAppearance
{
    // Dark editor colours match the TypeScript editor (#242938) on both stands.
    public static IHighlightingDefinition Cpp(bool dark=true)
    {
        const string xml="""
        <SyntaxDefinition name="Arduino" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Keyword" foreground="#C4B5FD" fontWeight="bold"/>
          <Color name="Comment" foreground="#98A2B8"/>
          <Color name="String" foreground="#B9DEA0"/>
          <Color name="Number" foreground="#F1D699"/>
          <RuleSet>
            <Span color="Comment" begin="//" end="\n"/>
            <Span color="Comment" begin="/\*" end="\*/" multiline="true"/>
            <Span color="String" begin="&quot;" end="&quot;"><RuleSet><Span begin="\\" end="."/></RuleSet></Span>
            <Keywords color="Keyword"><Word>void</Word><Word>int</Word><Word>bool</Word><Word>float</Word><Word>double</Word><Word>const</Word><Word>unsigned</Word><Word>long</Word><Word>if</Word><Word>else</Word><Word>for</Word><Word>while</Word><Word>return</Word><Word>HIGH</Word><Word>LOW</Word><Word>INPUT</Word><Word>OUTPUT</Word><Word>INPUT_PULLUP</Word><Word>true</Word><Word>false</Word></Keywords>
            <Rule color="Number">\b[0-9]+(\.[0-9]+)?\b</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;
        var definition=dark?xml:xml.Replace("#C4B5FD","#705CC6").Replace("#98A2B8","#6B7080").Replace("#B9DEA0","#357445").Replace("#F1D699","#885516");
        using var reader=System.Xml.XmlReader.Create(new StringReader(definition));return AvaloniaEdit.Highlighting.Xshd.HighlightingLoader.Load(reader,HighlightingManager.Instance);
    }
    public static IHighlightingDefinition CppForTheme()=>Cpp(ThemeManager.Ink(((Avalonia.Media.SolidColorBrush)Avalonia.Application.Current!.Resources["EditorBrush"]!).Color.ToString())=="#FFFFFF");
}
