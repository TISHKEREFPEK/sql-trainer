namespace Classroom.Desktop.Electronics;

public static class ElectronicsReviewLabels
{
    public static string State(string state) => state switch
    {
        "awaiting-review" => "Ожидает проверки",
        "accepted" => "Принято",
        "changes-requested" => "На доработку",
        "rejected" => "Не принято",
        "allow-resubmit" => "Пересдача разрешена",
        _ => "Черновик"
    };
}
