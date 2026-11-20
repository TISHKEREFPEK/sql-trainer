using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop;
using Xunit;

namespace Classroom.UiTests;

public sealed class GraphiteThemeTests
{
    [Fact]
    public async Task NewProfileGetsGraphiteAndExplicitLegacyAndCustomPreferencesSurviveRestart()
    {
        await using var fixture=await LiveClassroom.Start();
        Assert.Equal("graphite",(await fixture.Student.Login(new("student","ui-test","synthetic-student-password"))).Theme.Preset);
        // Exercise the server whitelist as well as the desktop list. None of
        // the previous preset IDs becomes invalid when the default changes.
        foreach(var id in ThemeManager.Presets.Keys)
            await fixture.Student.Put<JsonElement>("theme",new ThemePreference(id));
        var legacy=new ThemePreference("light",new(){["editor"]="#223344"});
        var custom=new ThemePreference("graphite",new(){["accent"]="#35464F",["surface"]="#FFFFFF"});
        await fixture.Student.Put<JsonElement>("theme",legacy);
        await fixture.Admin.Put<JsonElement>("theme",custom);
        await fixture.Admin.Post<ThemePreset[]>("theme-presets",new ThemePresetRequest("Графит кабинета",custom));
        await fixture.Restart();
        var teacher=await fixture.Admin.Login(new("teacher","","synthetic-teacher-password"));
        var lesson=(await fixture.Admin.Get<LessonDashboard>("teacher/lessons")).Current!;
        await fixture.Admin.Post<LessonView>($"teacher/lessons/{lesson.Id}/restore");
        var student=await fixture.Student.Login(new("student","ui-test","synthetic-student-password"));
        Assert.Equal("light",student.Theme.Preset);Assert.Equal("#223344",student.Theme.Colors!["editor"]);
        Assert.Equal("graphite",teacher.Theme.Preset);Assert.Equal("#35464F",teacher.Theme.Colors!["accent"]);
        Assert.Equal("#FFFFFF",Assert.Single(teacher.ThemePresets).Theme.Colors!["surface"]);
        Assert.Equal(400,(await Assert.ThrowsAsync<ApiException>(()=>fixture.Admin.Put<JsonElement>("theme",new ThemePreference("unknown")))).Status);
    }

    [AvaloniaFact]
    public async Task SwitchingGraphitePreservesTheEditorDocumentSelectionAndUndoHistory()
    {
        await using var fixture=await LiveClassroom.Start();
        ThemeManager.Apply(new("light"));
        var window=new MainWindow(false,false){Width=1366,Height=768};
        using var student=new StudentScreen(window,fixture.Student);
        try
        {
            window.Preview(student);window.Show();await student.Load();window.UpdateLayout();
            var editor=student.GetVisualDescendants().OfType<TextEditor>().Single();
            editor.Text="SELECT 1 AS first_draft;";
            editor.Document.UndoStack.ClearAll();
            editor.Document.Insert(editor.Document.TextLength,"\n-- сохранённая правка");
            editor.Select(7,6);
            var document=editor.Document;var text=editor.Text;var caret=editor.CaretOffset;
            var start=editor.SelectionStart;var length=editor.SelectionLength;
            foreach(var preference in new[]{new ThemePreference(),new("onyx"),new("graphite",new(){["editor"]="#17212B"}),new ThemePreference()})
            {
                ThemeManager.Apply(preference);window.UpdateLayout();
                Assert.Same(editor,student.GetVisualDescendants().OfType<TextEditor>().Single());
                Assert.Same(document,editor.Document);Assert.Equal(text,editor.Text);
                Assert.Equal(caret,editor.CaretOffset);Assert.Equal(start,editor.SelectionStart);Assert.Equal(length,editor.SelectionLength);
                Assert.True(document.UndoStack.CanUndo);
                Assert.True(Contrast(((SolidColorBrush)editor.Background!).Color,((SolidColorBrush)editor.Foreground!).Color)>=4.5);
            }
            using(var frame=window.CaptureRenderedFrame())
            {
                var output=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??AppContext.BaseDirectory;
                Directory.CreateDirectory(output);frame!.Save(Path.Combine(output,"graphite-foundation-sql-1366.png"));
            }
            document.UndoStack.Undo();Assert.Equal("SELECT 1 AS first_draft;",editor.Text);
        }
        finally{window.Close();ThemeManager.Apply(new());}
    }

    [AvaloniaFact]
    public void DefaultAndCustomGraphiteKeepInformativeTextReadable()
    {
        try
        {
            foreach(var preference in new[]{new ThemePreference(),new("graphite",new(){["surface"]="#000000",["editor"]="#000000"}),new("graphite",new(){["surface"]="#808080",["editor"]="#808080"})})
            {
                ThemeManager.Apply(preference);
                Color Resource(string name)=>((SolidColorBrush)Application.Current!.Resources[name+"Brush"]!).Color;
                foreach(var pair in new[]{("Ink","Surface"),("Muted","Surface"),("EditorInk","Editor"),("LineNumbers","Editor"),("SuccessInk","SuccessBackground"),("ErrorInk","ErrorBackground"),("InfoInk","InfoBackground")})
                    Assert.True(Contrast(Resource(pair.Item1),Resource(pair.Item2))>=4.5,$"{preference.Preset}: {pair.Item1}");
            }
        }
        finally{ThemeManager.Apply(new());}
    }

    static double Contrast(Color a,Color b)
    {
        static double Luminance(Color c){static double Linear(byte v){var n=v/255d;return n<=.04045?n/12.92:Math.Pow((n+.055)/1.055,2.4);}return .2126*Linear(c.R)+.7152*Linear(c.G)+.0722*Linear(c.B);}
        var x=Luminance(a);var y=Luminance(b);return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);
    }
}
