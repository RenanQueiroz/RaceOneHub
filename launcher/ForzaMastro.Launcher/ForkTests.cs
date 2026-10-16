using System.IO;
using System.Text.Json;

namespace ForzaMastro.Launcher;

// Fork-only (FORK.md): PLAY's fresh/stale decision, settings from the pre-game screen, swap and caches.
internal static class ForkTests
{
    internal static void Run(Action<bool,string> check, string testRoot)
    {
        var root=Path.Combine(testRoot,"fork-play");
        Directory.CreateDirectory(Path.Combine(root,"runtime","pc"));Directory.CreateDirectory(Path.Combine(root,"tools"));
        File.WriteAllText(Path.Combine(root,"runtime","pc","ForzaMastro5Bridge.dll"),"bridge one");
        var game=Path.Combine(testRoot,"fork game files");
        var wanted=new Dictionary<string,string>{["resolution"]="2160",["frame_rate"]="60",["antialiasing"]="msaax4",["graphics"]="ultra",
            ["display"]="fullscreen",["vsync"]="true",["language"]="en-US",["audio"]="multichannel",["show_pre_game"]="true"};
        void Output(string folder,string frameRate,string vsync,string? stamp)
        {
            Directory.CreateDirectory(Path.Combine(folder,"data","cache"));Directory.CreateDirectory(Path.Combine(folder,"data","logs"));
            File.WriteAllText(Path.Combine(folder,"RaceOne5.exe"),"");
            File.WriteAllText(Path.Combine(folder,"data","RaceOneEngine.ini"),$"[launcher]\ngame=7b35d917\ngame_folder={game}\nresolution=2160\nantialiasing=msaax4\ngraphics=ultra\nparticles=default\naudio=multichannel\nframe_rate={frameRate}\nvsync={vsync}\ndisplay=fullscreen\nlanguage=en-US\n\n[title-7b35d917]\nfolder={game}\n");
            File.WriteAllText(Path.Combine(folder,"forzamastro.ini"),"[startup]\nshow_pre_game=1\n");
            File.WriteAllText(Path.Combine(folder,"build-profile.json"),JsonSerializer.Serialize(new{settings=new{resolution=2160,frame_rate=60,antialiasing="msaax4",graphics="ultra",display="fullscreen",vsync=true,language="en-US",audio="multichannel",show_pre_game=true}}));
            if(stamp is not null) ForkOutput.WriteStamp(folder,stamp);
        }
        var output=Path.Combine(root,"builds","RaceOne5");
        var runtime=ForkOutput.RuntimeStamp(root);
        check(ForkOutput.StaleReason(wanted,game,output,runtime)=="No game built yet.","Fork: a missing game is built first");
        Output(output,"60","on",runtime);
        check(ForkOutput.StaleReason(wanted,game,output,runtime) is null,"Fork: an up-to-date game starts without a build");
        check(ForkOutput.ReadEffective(output)!["vsync"]=="true" && ForkOutput.ReadEffective(output)!["show_pre_game"]=="true","Fork: ini on/1 read as the Hub's true");
        check(ForkOutput.StaleReason(new Dictionary<string,string>(wanted){["graphics"]="high"},game,output,runtime)=="Settings changed.","Fork: a changed setting rebuilds");
        check(ForkOutput.StaleReason(wanted,game+" elsewhere",output,runtime)=="The game folder changed.","Fork: another game folder rebuilds");
        File.WriteAllText(Path.Combine(root,"runtime","pc","ForzaMastro5Bridge.dll"),"bridge two");
        var changed=ForkOutput.RuntimeStamp(root);
        check(changed!=runtime && ForkOutput.StaleReason(wanted,game,output,changed)=="The runtime changed.","Fork: a new bridge rebuilds");
        // The pre-game screen changed the frame rate after the build.
        Output(output,"30","on",runtime);
        var effective=ForkOutput.ReadEffective(output)!;var built=ForkOutput.ReadBuilt(output)!;
        check(effective["frame_rate"]=="30" && built["frame_rate"]=="60" && built["vsync"]=="true","Fork: pre-game change visible against the build profile");
        check(ForkOutput.Signature(output,effective)!=ForkOutput.Signature(output,ForkOutput.ReadEffective(Path.Combine(root,"none"))??wanted),"Fork: signature follows the game's settings");
        // Rebuild: caches carry over only from the same runtime, logs always.
        File.WriteAllText(Path.Combine(output,"data","cache","pipelines.bin"),"cache");File.WriteAllText(Path.Combine(output,"data","logs","RaceOneEngine.log"),"log");
        var next=ForkOutput.NextFolder(output);Output(next,"60","on",runtime);
        ForkOutput.Carry(output,next,runtime);
        check(File.Exists(Path.Combine(next,"data","cache","pipelines.bin")) && File.Exists(Path.Combine(next,"data","logs","RaceOneEngine.log")),"Fork: same runtime keeps caches and logs");
        var other=Path.Combine(root,"other.next");Output(other,"60","on",changed);
        ForkOutput.Carry(output,other,changed);
        check(!File.Exists(Path.Combine(other,"data","cache","pipelines.bin")) && File.Exists(Path.Combine(other,"data","logs","RaceOneEngine.log")),"Fork: new runtime drops caches, keeps logs");
        ForkOutput.Swap(next,output);
        check(!Directory.Exists(next) && !Directory.Exists(ForkOutput.OldFolder(output)) && ForkOutput.ReadEffective(output)!["frame_rate"]=="60","Fork: swap puts the new build in place");
        try{ForkOutput.Swap(Path.Combine(root,"missing.next"),output);check(false,"Fork: failed swap accepted");}
        catch(DirectoryNotFoundException){check(Directory.Exists(output) && ForkOutput.ReadEffective(output) is not null,"Fork: failed swap restores the previous build");}
        check(!ForkOutput.IsRunning(Path.Combine(output,"RaceOne5.exe")),"Fork: no game running from a test folder");
    }
}
