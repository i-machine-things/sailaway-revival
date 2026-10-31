// Sailaway offline patcher: rewrites the user's own Assembly-CSharp.dll so the
// game talks to a replacement server and no longer needs the dead multiplayer
// server to start. Always patches from the pristine backup (.orig).
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: patcher <Sailaway_Data/Managed dir> [server host[:port][/prefix]]");
    return 1;
}
string managed = args[0];
string server = args.Length > 1 ? args[1] : "127.0.0.1:18080/sw";
string dll = Path.Combine(managed, "Assembly-CSharp.dll");
string orig = dll + ".orig";
if (!File.Exists(orig))
{
    File.Copy(dll, orig);
    Console.WriteLine("backup created: " + orig);
}

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(managed);
var asm = AssemblyDefinition.ReadAssembly(orig, new ReaderParameters { AssemblyResolver = resolver });
var module = asm.MainModule;
int changes = 0;

// 1. Point every hard-coded server address at the replacement server.
foreach (var type in module.GetTypes())
foreach (var method in type.Methods.Where(m => m.HasBody))
foreach (var ins in method.Body.Instructions)
{
    if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s && s.Contains("srv.sailaway.world"))
    {
        ins.Operand = s.Replace("srv.sailaway.world", server);
        Console.WriteLine($"  url: {type.Name}.{method.Name}: {s} -> {ins.Operand}");
        changes++;
    }
}

// 2. Time sync only ever came from the multiplayer server; the clock already
//    starts from local UTC, so report it as synced.
var sm = module.GetType("StateMachine");
var getter = sm.Properties.First(p => p.Name == "timeSynced").GetMethod;
var il = getter.Body.GetILProcessor();
getter.Body.Instructions.Clear();
getter.Body.ExceptionHandlers.Clear();
il.Emit(OpCodes.Ldc_I4_1);
il.Emit(OpCodes.Ret);
Console.WriteLine("  StateMachine.timeSynced -> always true");
changes++;

// 3. Boat control (lines, helm, teleport) is gated on mastership, which only the
//    multiplayer server ever granted. Offline the player is always the master.
var userBoat = module.GetType("UserBoat");
var master = userBoat.Properties.First(p => p.Name == "hasMastership").GetMethod;
il = master.Body.GetILProcessor();
master.Body.Instructions.Clear();
master.Body.ExceptionHandlers.Clear();
il.Emit(OpCodes.Ldc_I4_1);
il.Emit(OpCodes.Ret);
Console.WriteLine("  UserBoat.hasMastership -> always true");
changes++;

// 4. Grabbing a line or the helm first locks it against other crew. The lock
//    fails offline because the multiplayer controller never learns our boat
//    number, so every manual adjustment was refused. Single player: always grant.
var mu = module.GetType("MultiuserCtrl");
var lockControl = mu.Methods.First(m => m.Name == "LockControl");
il = lockControl.Body.GetILProcessor();
lockControl.Body.Instructions.Clear();
lockControl.Body.ExceptionHandlers.Clear();
lockControl.Body.Variables.Clear();
il.Emit(OpCodes.Ldc_I4_1);
il.Emit(OpCodes.Ret);
Console.WriteLine("  MultiuserCtrl.LockControl -> always true");
changes++;

// Write beside the DLL and swap it in, so a half-written file never replaces it.
string tmp = dll + ".tmp";
asm.Write(tmp);
asm.Dispose();
try
{
    File.Move(tmp, dll, overwrite: true);
}
catch (IOException e)
{
    File.Delete(tmp);
    Console.Error.WriteLine($"could not replace {dll} ({e.Message}). Close Sailaway and run the patcher again.");
    return 2;
}
Console.WriteLine($"done, {changes} changes written to {dll}");
return 0;
