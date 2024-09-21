/*
 * Author: Misaka Mikoto
 * email: easy66@live.com
 * github: https://github.com/Misaka-Mikoto-Tech/UnityScriptHotReload
 */

using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace AssemblyPatcher;
public class SourceCompiler
{
    const int kMaxCompileTime = 60 * 1000;

    public string moduleName { get; private set; }
    public string outputPath { get; private set; }

    private string _rspPath;
    private static string s_CS_File_Path__Patch_Assembly_Attr__;            // __Patch_Assembly_Attr__.cs
    private static string s_CS_File_Path__Methods_For_Patch_Wrapper__Gen__; // __Methods_For_Patch_Wrapper__Gen__.cs

    private List<string> _filesToCompile = new List<string>();

    static SourceCompiler()
    {
        s_CS_File_Path__Patch_Assembly_Attr__ = GlobalConfig.Instance.tempScriptDir + $"/__Patch_Assembly_Attr__.cs";
        s_CS_File_Path__Methods_For_Patch_Wrapper__Gen__ = GlobalConfig.Instance.tempScriptDir + $"/__Methods_For_Patch_Wrapper__Gen__.cs";

        GenCSFile__Patch_Assembly_Attr__();
        GenCSFile__Methods_For_Patch_Wrapper__Gen__();
    }
    
    public SourceCompiler(string moduleName)
    {
        this.moduleName = moduleName;
        outputPath = string.Format(GlobalConfig.Instance.patchDllPathFormat, this.moduleName, GlobalConfig.Instance.patchNo);
    }
    
    public int DoCompile()
    {
        Utils.DeleteFileWithRetry(outputPath);
        Utils.DeleteFileWithRetry(Path.ChangeExtension(outputPath, ".pdb"));

        _rspPath = GlobalConfig.Instance.tempScriptDir + $"/__{moduleName}_Patch.rsp";

        GetAllFilesToCompile();
        GenRspFile();
        int retCode = RunDotnetCompileProcess();
        return retCode;
    }

    /// <summary>
    /// 创建文件 __Patch_Assembly_Attr__.cs
    /// </summary>
    static void GenCSFile__Patch_Assembly_Attr__()
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Diagnostics;");
        sb.AppendLine("using System.Reflection;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Security.Permissions;");
        sb.AppendLine();
        sb.AppendLine("[assembly: Debuggable(DebuggableAttribute.DebuggingModes.Default | DebuggableAttribute.DebuggingModes.DisableOptimizations | DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints | DebuggableAttribute.DebuggingModes.EnableEditAndContinue)]");

#if FOR_NET6_0_OR_GREATER
        // for .netcore or newer
        sb.AppendLine($"[assembly: IgnoresAccessChecksTo(\"{_moduleName}\")]");
#else
        // for .net framework
        sb.AppendLine("[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]");
#endif

        File.WriteAllText(s_CS_File_Path__Patch_Assembly_Attr__, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// 创建文件 __Patch_GenericInst_Wrapper__Gen__.cs
    /// </summary>
    static void GenCSFile__Methods_For_Patch_Wrapper__Gen__()
    {
        string text =
@"using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ScriptHotReload
{
    /// <summary>
    /// 用于 AssemblyPatcher 生成非泛型和泛型实例定义的 wrapper 类型
    /// </summary>
    public class __Methods_For_Patch_Wrapper__Gen__
    {
        /// <summary>
        /// 扫描 base dll 里所有的方法，然后获取与之关联的 patch dll 内创建的 wrapper 函数
        /// </summary>
        /// <returns></returns>
        public static Dictionary<MethodBase, MethodBase> GetMethodsForPatch()
        {
            // 函数体会被 Assembly Patcher 替换
            throw new NotImplementedException();
        }

        /// <summary>
        /// 当 patch dll 内所有的函数均未定义局部变量时，#Strings heap 不会被dnlib写入pdb文件中，但mono认为此heap必定存在，且会去检验，这会导致crash
        /// 因此我们这里放一个无用的局部变量强制创建 #Strings heap
        /// </summary>
        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static string ___UnUsed_Method_To_Avoid_Dnlib_Bug___(string str)
        {
            string uselessVar = str + ""This is a useless variable used to avoid dnlib's bug, please don't remove it!"";
            return uselessVar;
        }
    }
}
";
        File.WriteAllText(s_CS_File_Path__Methods_For_Patch_Wrapper__Gen__, text, Encoding.UTF8);
    }

    void GenRspFile()
    {
        var sb = new StringBuilder();
        sb.AppendLine("-target:library");
        sb.AppendLine($"-out:\"{outputPath}\"");
        foreach(var def in GlobalConfig.Instance.defines)
            sb.AppendLine($"-define:{def}");
        foreach(var @ref in GlobalConfig.Instance.assemblyPathes.Values)
            sb.AppendLine($"-r:\"{@ref}\"");

#if FOR_NET6_0_OR_GREATER
        sb.AppendLine($"-r:\"{typeof(IgnoresAccessChecksToAttribute).Assembly.Location}\"");
#endif
        sb.AppendLine($"-langversion:latest");

        sb.AppendLine("/unsafe");
        sb.AppendLine("/deterministic");
        sb.AppendLine("/optimize-");
        sb.AppendLine("/debug:portable");
        sb.AppendLine("/nologo");
        sb.AppendLine("/RuntimeMetadataVersion:v4.0.30319");

        sb.AppendLine("/nowarn:0169");
        sb.AppendLine("/nowarn:0649");
        sb.AppendLine("/nowarn:1701");
        sb.AppendLine("/nowarn:1702");
        // obsolete warning
        sb.AppendLine("/nowarn:0618");
        // type defined in source files conficts with imported type at ref dll, using type in source file
        sb.AppendLine("/nowarn:0436");
        sb.AppendLine("/utf8output");
        sb.AppendLine("/preferreduilang:en-US");

        sb.AppendLine($"\"{s_CS_File_Path__Patch_Assembly_Attr__}\"");
        sb.AppendLine($"\"{s_CS_File_Path__Methods_For_Patch_Wrapper__Gen__}\"");
        foreach (var src in _filesToCompile)
            sb.AppendLine($"\"{src}\"");

        File.WriteAllText(_rspPath, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// 获取所有需要编译的文件，包括已改变的文件和可能的partial class所在的其它文件（只参与编译不会被hook）
    /// </summary>
    /// <remarks>需要像生成泛型pair一样生成返回所有hook pair的方法，而不是让主程序自己反射去读取，因为反射无法获取方法所在文件</remarks>
    void GetAllFilesToCompile()
    {
        var fileChanged = GlobalConfig.Instance.filesToCompile[moduleName];
        var defines = GlobalConfig.Instance.defines;
        var partialClassScanner = new PartialClassScanner(moduleName, fileChanged, defines);
        partialClassScanner.Scan();

        _filesToCompile.Clear();
        _filesToCompile.AddRange(fileChanged);
        _filesToCompile.AddRange(partialClassScanner.allFilesNeeded);
        _filesToCompile = new List<string>(_filesToCompile.Distinct());
    }

    int RunDotnetCompileProcess()
    {
        try
        {
            var references = new List<PortableExecutableReference>();

            foreach (var referenceFile in GlobalConfig.Instance.assemblyPathes.Values)
            {
                references.Add(MetadataReference.CreateFromFile(referenceFile));
            }
            var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).
                    WithMetadataImportOptions(MetadataImportOptions.All)
                    .WithOptimizationLevel(OptimizationLevel.Debug);
            var topLevelBinderFlagsProperty = typeof(CSharpCompilationOptions).GetProperty("TopLevelBinderFlags", BindingFlags.Instance | BindingFlags.NonPublic);
            topLevelBinderFlagsProperty.SetValue(compilationOptions, (uint)1 << 22);
            SyntaxTree[] codeTree = new CSharpSyntaxTree[_filesToCompile.Count + 2];
            var parseOptions = new CSharpParseOptions().WithPreprocessorSymbols(GlobalConfig.Instance.defines);
            for (int i = 0; i < _filesToCompile.Count; i++)
            {
                codeTree[i] = CSharpSyntaxTree.ParseText(File.ReadAllText(_filesToCompile[i]), parseOptions,path: _filesToCompile[i], encoding: Encoding.UTF8);
            }
            codeTree[_filesToCompile.Count] = CSharpSyntaxTree.ParseText(File.ReadAllText(s_CS_File_Path__Patch_Assembly_Attr__), parseOptions, path: s_CS_File_Path__Patch_Assembly_Attr__, encoding: Encoding.UTF8);
            codeTree[_filesToCompile.Count+1] = CSharpSyntaxTree.ParseText(File.ReadAllText(s_CS_File_Path__Methods_For_Patch_Wrapper__Gen__), parseOptions, path: s_CS_File_Path__Methods_For_Patch_Wrapper__Gen__, encoding: Encoding.UTF8);
            var compilation = CSharpCompilation.Create(Utils.GetPatchDllName(moduleName),
                    codeTree, references,
                    compilationOptions);
            using var dllStream = new FileStream(outputPath, FileMode.Create);
            using var pdbStream = new FileStream(Path.ChangeExtension(outputPath, ".pdb"), FileMode.Create);
            var emitOptions = new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb);
            var result = compilation.Emit(dllStream, pdbStream, options: emitOptions);
            return result.Success == true ? 0 : 1;
        }
        catch (Exception ex) 
        {
            Debug.LogError(ex.ToString());
            return 1;
        }
    }

    private void Process_OutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        throw new NotImplementedException();
    }
}
