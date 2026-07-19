using System.IO;

namespace 币安量化机器人.Services;

public static class AppDataPaths
{
    public static string RootDirectory { get; }=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WPE Agent");
    public static string DataDirectory { get; }=Ensure(Path.Combine(RootDirectory,"Data"));
    public static string File(string name)
    {
        var target=Path.Combine(DataDirectory,name);
        var legacy=Path.Combine(AppContext.BaseDirectory,"Data",name);
        if(!System.IO.File.Exists(target)&&System.IO.File.Exists(legacy))System.IO.File.Copy(legacy,target,false);
        return target;
    }
    private static string Ensure(string path){Directory.CreateDirectory(path);return path;}
}
