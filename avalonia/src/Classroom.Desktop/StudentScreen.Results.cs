using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Classroom.Contracts;

namespace Classroom.Desktop;

public sealed partial class StudentScreen
{
    readonly TextBlock resultCount=Ui.Muted("",12),resultChanged=Ui.Text("",12);
    string? resultCode;
    string resultCounter="";
    void ResetSqlResult()
    {
        resultCode=null;resultCounter="";resultCount.Text="";
        var empty=Ui.Muted("Результат появится после запуска запроса",14);empty.VerticalAlignment=VerticalAlignment.Center;
        result.Content=empty;UpdateResultStale();
    }
    void UpdateResultStale()
    {
        resultChanged.IsVisible=resultCode is not null&&resultCode!=editor.Text;
        resultChanged.Text=resultChanged.IsVisible?"Запрос изменён. Здесь результат предыдущего запуска.":"";
    }
    void UpdateResultCount()=>resultCount.Text=executing?"Выполняется…":resultCounter;
    static Border ResultNotice(string text,string tone)
    {
        var label=Ui.Text(text,12);Ui.BindBrush(label,TextBlock.ForegroundProperty,tone+"Ink");
        var notice=new Border{Child=label,Padding=new Thickness(10,8),CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1)};
        Ui.BindBrush(notice,Border.BackgroundProperty,tone+"Background");Ui.BindBrush(notice,Border.BorderBrushProperty,tone+"Line");return notice;
    }
    void ShowSqlResult(ExecutionDto response,string code,bool checking,string? taskId=null)
    {
        resultCode=code;resultCounter=response.Error is not null?"Ошибка":$"Строк: {response.Output.Values.Length}";resultCount.Text=resultCounter;
        if(response.Error is not null)
        {
            var exact=Ui.Text(response.Error,12);exact.FontFamily=new FontFamily("Consolas, monospace");
            result.Content=Ui.Scroll(Ui.Stack(exact,ResultNotice("Запрос не выполнен.\n"+(response.Explanation??""),"Error")));
        }
        else
        {
            var view=new Grid{RowDefinitions=new("*,Auto"),RowSpacing=8};view.Children.Add(Ui.Table(response.Output,fill:true));
            if(checking)
            {
                var feedback=Ui.Stack(ResultNotice(response.Correct?"Задание выполнено":response.Explanation??"Результат отличается от ожидаемого.",response.Correct?"Success":"Info"));
                var next=response.Correct&&taskId is not null?catalogue?.Tasks.SkipWhile(t=>t.Id!=taskId).Skip(1).FirstOrDefault():null;
                if(next is not null)feedback.Children.Add(Ui.Button("Следующее задание",()=>owner.Safe(async()=>
                {
                    category.SelectedItem=Category(next);module.SelectedItem=next.Module;search.Text="";await Select(next.Id);
                })));
                var feedbackScroll = Ui.Scroll(feedback);
                feedbackScroll.MaxHeight = 110;
                Grid.SetRow(feedbackScroll,1);view.Children.Add(feedbackScroll);
            }
            result.Content=view;
        }
        UpdateResultStale();
    }
}
