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

asm.Write(dll);
Console.WriteLine($"done, {changes} changes written to {dll}");
return 0;
