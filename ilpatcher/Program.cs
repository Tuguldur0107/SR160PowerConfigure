using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: IlPatcher <SR160PowerConfig.dll>");
    Environment.Exit(2);
}

var target = Path.GetFullPath(args[0]);
var backup = target + ".bak";
if (!File.Exists(backup))
{
    File.Copy(target, backup);
}

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(target)!);
resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(object).Assembly.Location)!);

var readerParameters = new ReaderParameters
{
    ReadWrite = false,
    ReadingMode = ReadingMode.Immediate,
    AssemblyResolver = resolver
};

using var input = new MemoryStream(File.ReadAllBytes(target));
var assembly = AssemblyDefinition.ReadAssembly(input, readerParameters);
var module = assembly.MainModule;
var keyboardType = module.Types.First(t => t.FullName == "SR160PowerConfig.WindowsKeyboard");
var method = keyboardType.Methods.First(m => m.Name == "PlayDefaultBeep");
var messageBeep = keyboardType.Methods.First(m => m.Name == "MessageBeep");
var consoleBeep = module.ImportReference(
    typeof(Console).GetMethod(nameof(Console.Beep), new[] { typeof(int), typeof(int) })!);
var exceptionType = module.ImportReference(typeof(Exception));

method.Body.ExceptionHandlers.Clear();
method.Body.Variables.Clear();
method.Body.Instructions.Clear();
method.Body.InitLocals = false;
method.Body.MaxStackSize = 2;

var il = method.Body.GetILProcessor();
var tryStart = Instruction.Create(OpCodes.Ldc_I4, 950);
var handlerStart = Instruction.Create(OpCodes.Pop);
var ret = Instruction.Create(OpCodes.Ret);
var tryEnd = Instruction.Create(OpCodes.Leave_S, ret);
var handlerEnd = Instruction.Create(OpCodes.Leave_S, ret);

il.Append(tryStart);
il.Append(Instruction.Create(OpCodes.Ldc_I4, 120));
il.Append(Instruction.Create(OpCodes.Call, consoleBeep));
il.Append(tryEnd);
il.Append(handlerStart);
il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
il.Append(Instruction.Create(OpCodes.Call, messageBeep));
il.Append(Instruction.Create(OpCodes.Pop));
il.Append(handlerEnd);
il.Append(ret);

method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
{
    TryStart = tryStart,
    TryEnd = handlerStart,
    HandlerStart = handlerStart,
    HandlerEnd = ret,
    CatchType = exceptionType
});

var mainWindowType = module.Types.First(t => t.FullName == "SR160PowerConfig.MainWindow");
var boolType = module.TypeSystem.Boolean;
var intType = module.TypeSystem.Int32;
var suppressKeyMethod = new MethodDefinition(
    "IsExternalOutputDataKey",
    MethodAttributes.Public | MethodAttributes.Static,
    boolType);
suppressKeyMethod.Parameters.Add(new ParameterDefinition("virtualKey", ParameterAttributes.None, intType));
keyboardType.Methods.Add(suppressKeyMethod);

var suppressIl = suppressKeyMethod.Body.GetILProcessor();
var trueLoad = Instruction.Create(OpCodes.Ldc_I4_1);
var trueRet = Instruction.Create(OpCodes.Ret);
var falseLoad = Instruction.Create(OpCodes.Ldc_I4_0);
var falseRet = Instruction.Create(OpCodes.Ret);
var checkNumpad = Instruction.Create(OpCodes.Ldarg_0);
var checkHex = Instruction.Create(OpCodes.Ldarg_0);
var checkEnter = Instruction.Create(OpCodes.Ldarg_0);
suppressKeyMethod.Body.MaxStackSize = 2;
suppressIl.Append(Instruction.Create(OpCodes.Ldarg_0));
suppressIl.Append(Instruction.Create(OpCodes.Ldc_I4_S, (sbyte)0x30));
suppressIl.Append(Instruction.Create(OpCodes.Blt_S, checkNumpad));
suppressIl.Append(Instruction.Create(OpCodes.Ldarg_0));
suppressIl.Append(Instruction.Create(OpCodes.Ldc_I4_S, (sbyte)0x39));
suppressIl.Append(Instruction.Create(OpCodes.Ble_S, trueLoad));
suppressIl.Append(checkNumpad);
suppressIl.Append(Instruction.Create(OpCodes.Ldc_I4_S, (sbyte)0x60));
suppressIl.Append(Instruction.Create(OpCodes.Blt_S, checkHex));
suppressIl.Append(Instruction.Create(OpCodes.Ldarg_0));
suppressIl.Append(Instruction.Create(OpCodes.Ldc_I4_S, (sbyte)0x69));
suppressIl.Append(Instruction.Create(OpCodes.Ble_S, trueLoad));
suppressIl.Append(checkHex);
suppressIl.Append(Instruction.Create(OpCodes.Ldc_I4_S, (sbyte)0x41));
suppressIl.Append(Instruction.Create(OpCodes.Blt_S, checkEnter));
suppressIl.Append(Instruction.Create(OpCodes.Ldarg_0));
suppressIl.Append(Instruction.Create(OpCodes.Ldc_I4_S, (sbyte)0x46));
suppressIl.Append(Instruction.Create(OpCodes.Ble_S, trueLoad));
suppressIl.Append(checkEnter);
suppressIl.Append(Instruction.Create(OpCodes.Ldc_I4_S, (sbyte)0x0D));
suppressIl.Append(Instruction.Create(OpCodes.Beq_S, trueLoad));
suppressIl.Append(falseLoad);
suppressIl.Append(falseRet);
suppressIl.Append(trueLoad);
suppressIl.Append(trueRet);

var onGlobalKeyDown = mainWindowType.Methods.First(m => m.Name == "OnGlobalKeyDown");
var onGlobalInstructions = onGlobalKeyDown.Body.Instructions;
var finalReturnLoad = onGlobalInstructions.Last(i => i.OpCode == OpCodes.Ldc_I4_0);
finalReturnLoad.OpCode = OpCodes.Ldarg_1;
finalReturnLoad.Operand = null;
onGlobalKeyDown.Body.GetILProcessor().InsertAfter(
    finalReturnLoad,
    Instruction.Create(OpCodes.Call, suppressKeyMethod));

var buzzerModeMethod = mainWindowType.Methods.First(m => m.Name == "ApplyScannerBuzzerForReadMode");
var setBuzzerCallIndex = buzzerModeMethod.Body.Instructions
    .Select((instruction, index) => new { instruction, index })
    .First(x => x.instruction.OpCode == OpCodes.Callvirt
        && x.instruction.Operand is MethodReference methodReference
        && methodReference.Name == "SetBuzzer")
    .index;

var instructions = buzzerModeMethod.Body.Instructions;
// Original stable code passes !rbSingle to SetBuzzer(enabled, save), which
// turns the scanner buzzer off in Single. Keep the method and flow intact,
// but force the enabled argument to true.
instructions[setBuzzerCallIndex - 4].OpCode = OpCodes.Ldc_I4_1;
instructions[setBuzzerCallIndex - 4].Operand = null;
instructions[setBuzzerCallIndex - 3].OpCode = OpCodes.Nop;
instructions[setBuzzerCallIndex - 3].Operand = null;
instructions[setBuzzerCallIndex - 2].OpCode = OpCodes.Nop;
instructions[setBuzzerCallIndex - 2].Operand = null;

assembly.Write(target);
Console.WriteLine($"Patched {target}");
Console.WriteLine($"Backup  {backup}");
