﻿/*
 * Author: Misaka Mikoto
 * email: easy66@live.com
 * github: https://github.com/Misaka-Mikoto-Tech/UnityScriptHotReload
 */

using SimpleJSON;
using System.Text;

namespace AssemblyPatcher;

public class GlobalConfig
{
    public static GlobalConfig Instance;

    public const string kWrapperClassFullName = "ScriptHotReload.__Methods_For_Patch_Wrapper__Gen__";

    public int patchNo;
    public string workDir;
    public string dotnetPath;
    public string cscPath;
    
    public string tempScriptDir;
    public string builtinAssembliesDir;
    public string patchDllPathFormat;
    public string lambdaWrapperBackend;

    /// <summary>
    /// 需要编译的文件, key: AssemblyName, value: FileList
    /// </summary>
    public Dictionary<string, List<string>> filesToCompile;

    public string[] defines;
    public Dictionary<string, string> assemblyPathes; // (name, path)
    public Dictionary<string, string> userAssemblyPathes; // 非系统和Unity相关的用户自己的dll
    public HashSet<string> searchPaths;


    public static void LoadFromFile(string inputFilePath)
    {
        JSONNode root = JSON.Parse(File.ReadAllText(inputFilePath, Encoding.UTF8));
        GlobalConfig config = new GlobalConfig();
        config.patchNo = root["patchNo"];
        config.workDir = root["workDir"];
        config.dotnetPath = root["dotnetPath"];
        config.cscPath = root["cscPath"];
        config.tempScriptDir = root["tempScriptDir"];
        config.builtinAssembliesDir = root["builtinAssembliesDir"];
        config.patchDllPathFormat = root["patchDllPathFormat"];
        config.lambdaWrapperBackend = root["lambdaWrapperBackend"];

        config.defines = root["defines"];
        string[] allAsses = root["allAssemblyPathes"];
        config.assemblyPathes = new Dictionary<string, string>();
        config.userAssemblyPathes = new Dictionary<string, string>();
        config.searchPaths = new HashSet<string>();

        config.filesToCompile = new Dictionary<string, List<string>>();
        foreach(var str in (string[])root["filesChanged"])
        {
            string[] kv = str.Split(':');
            string filePath = kv[0];
            string assemblyName = kv[1];
            if(!config.filesToCompile.TryGetValue(assemblyName, out var files))
            {
                files = new List<string>();
                config.filesToCompile.Add(assemblyName, files);
            }
            files.Add(filePath);
        }

        foreach (string ass in allAsses)
        {
            string fileNameNoExt = Path.GetFileNameWithoutExtension(ass);
            config.assemblyPathes.TryAdd(fileNameNoExt, ass);

            if (!ass.Contains("Library/ScriptAssemblies"))
                config.searchPaths.Add(Path.GetDirectoryName(ass));

            // 默认认为用户不会修改unity官方代码, 可根据自己需求自行调整
            if(ass.StartsWith(config.workDir) && !ass.Contains("/com.unity."))
            {
                if (!fileNameNoExt.StartsWith("Unity")
                    && !fileNameNoExt.StartsWith("System."))
                        config.userAssemblyPathes.Add(fileNameNoExt, ass);
            }
        }

        Instance = config;
    }
}
