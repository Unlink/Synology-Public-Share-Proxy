namespace ShareProxy;

// Synthetic local QA data. This class never talks to DSM and is unavailable outside Development.
public static class Demo
{
    public static readonly PublicFile[] Files =
    [
        new("demo_folder_assets_01", "Fotografie", true, null, 1791626400),
        new("demo_file_brief_0001", "Projektový brief.pdf", false, 2458624, 1791626400),
        new("demo_file_plan_00002", "Pôdorys — verzia 03.pdf", false, 6421870, 1791532800),
        new("demo_file_budget_003", "Rozpočet.xlsx", false, 156672, 1791446400),
        new("demo_file_notes_0004", "Poznámky zo stretnutia.txt", false, 3208, 1791360000),
        new("demo_file_archive_05", "Podklady.zip", false, 18743296, 1791273600)
    ];
    public static ShareInfo Info(string token, bool unlocked, ProxyOptions options)
    {
        if (token == "demo-expired") throw new ProxyException(410, "unavailable");
        if (token == "demo-password" && !unlocked) throw new ProxyException(401, "password_required");
        if (token is not ("demo-share" or "demo-upload" or "demo-password")) throw new ProxyException(404, "unavailable");
        return new ShareInfo(token == "demo-upload" ? "Podklady od vás" : "Projekt Atelier", token == "demo-upload" ? "Nahrajte sem fotografie a dokumenty k projektu. Ďakujeme." : "Podklady a dokumenty k nášmu spoločnému projektu.",
            token == "demo-upload" ? "upload" : "download", "2026-11-30", token == "demo-password", options.MaxUploadBytes, []);
    }
}
