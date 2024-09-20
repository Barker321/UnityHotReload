﻿//#define PATCHER_DEBUG

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MonoHook;
using DotNetDetour;
using System.Threading;

namespace ScriptHotReload
{
    [InitializeOnLoad]
    public class HotReloadExecutor
    {
        const string kMenue_HotReload = "Tools/HotReload/是否自动重载";

        public static int patchNo { get; private set; }
        public static List<string> patchDlls { get; private set; }
        static string _dotnetPath;
        static string _cscPath;

        static volatile int _patchState = 100;
        static object _patchTaskOutputLock = new object();
        static Queue<string> _patchTaskOutput = new Queue<string>();
        static Dictionary<string, List<MethodBase>> _methodsToHook = new Dictionary<string, List<MethodBase>>(); // <AssemblyName, List>

        /// <summary>
        /// 是否是自动重载模式
        /// </summary>
        public static bool autoReloadMode
        {
            get
            {
                return EditorPrefs.GetBool(kMenue_HotReload, false);
            }
            private set
            {
                EditorPrefs.SetBool(kMenue_HotReload, value);
            }
        }

        #region 菜单功能
        /// <summary>
        /// 重载事件是否已触发（auto模式下将始终触发）
        /// </summary>
        static bool reloadEventFired;

        [MenuItem("Tools/HotReload/立即重载 (Play时有效) #R")]
        static void Menu_ManualReload()
        {
            reloadEventFired = true;
        }

        /// <summary>
        /// 切换 [自动重载] 菜单函数
        /// </summary>
        [MenuItem(kMenue_HotReload, false)] // "Tools/HotReload/是否自动重载"
        static void Menue_SwapAutoReloadMode()
        {
            bool isChecked = Menu.GetChecked(kMenue_HotReload);
            isChecked = !isChecked;

            autoReloadMode = isChecked;
            Menu.SetChecked(kMenue_HotReload, isChecked);

            // 切换到自动模式时主动设置触发初始值为true, 反之手动模式初始不触发
            reloadEventFired = isChecked;
        }

        [MenuItem(kMenue_HotReload, true)]
        static bool Menue_AutoReloadMode_Check()
        {
            Menu.SetChecked(kMenue_HotReload, autoReloadMode);
            return true;
        }
        #endregion

        static HotReloadExecutor()
        {
            patchNo = 0;
            patchDlls = new List<string>();

            if (!HotReloadConfig.hotReloadEnabled)
                return;

            patchNo = 0;

            string dotnetName = "dotnet";
            string cscName = "csc.dll";
#if UNITY_EDITOR_WIN
            dotnetName += ".exe";
#endif
            var unityEditorPath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            UnityEngine.Debug.Log(unityEditorPath);
            _dotnetPath = Directory.GetFiles(unityEditorPath, dotnetName, SearchOption.AllDirectories).FirstOrDefault().Replace('\\', '/');

            // unity 2020 里有两个 csc.dll，我们选择Tools目录下的
            _cscPath = (from f in Directory.GetFiles(unityEditorPath, cscName, SearchOption.AllDirectories) 
                        let dir = Path.GetDirectoryName(f) 
                        where dir.Contains("Tools")
                        || dir.Contains("DotNetSdkRoslyn")
                        select f)
                .FirstOrDefault().Replace('\\', '/');

            UnityEngine.Debug.Assert(!string.IsNullOrEmpty(_dotnetPath));
            UnityEngine.Debug.Assert(!string.IsNullOrEmpty(_cscPath));

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnEditorUpdate;

            // hack: 提前在主线程初始下Hook相关静态类，因为下面我们将在子线程中执行Hook, 而Unity相关函数只允许在主线程调用
            HookUtils.GetPageAlignedAddr(1234, 10);
            LDasm.IsiOS();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange mode)
        {
            switch (mode)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    // 进入播放模式后，首先删除所有之前残留的patch文件
                    foreach(string file in Directory.GetFiles(HotReloadConfig.kTempScriptDir))
                    {
                        if (file.EndsWith(".dll") || file.EndsWith(".pdb"))
                        {
                            try
                            {
                                File.Delete(file);
                            }
                            catch(Exception ex)
                            {
                                UnityEngine.Debug.LogError(string.Format("delete patch file fail:{0}", ex.Message));
                            }
                        }
                            
                    }
                    break;
                default: break;
            }
        }

        private static void OnEditorUpdate()
        {
            if (LDasm.IsiOS() || !(HotReloadConfig.hotReloadEnabled && Application.isPlaying))
                return;

            if (_patchState != 200 && _patchState != 100)
            {
                DispatchTaskOutput();
                if (_patchState != 200)
                {
                    if(_patchState == 0)
                    {
                        try
                        {
                            patchNo++;
                            UnityEngine.Debug.Log("<color=yellow>热重载完成</color>");
                        }
                        catch(Exception ex)
                        {
                            UnityEngine.Debug.LogErrorFormat("热重载出错:{0}\r\n{1}", ex.Message, ex.StackTrace);
                        }
                    }
                    else
                    {
                        UnityEngine.Debug.LogErrorFormat("生成Patch出错, 停止重载");
                    }
                    _patchState = 100;
                }
                return;
            }

            if(autoReloadMode)
            {
                if (!FileWatcher.changedSinceLastGet
                || new TimeSpan(DateTime.Now.Ticks - FileWatcher.lastModifyTime.Ticks).TotalSeconds < HotReloadConfig.kAutoReloadPatchCheckSpan)
                    return;
            }
            else
            {
                try
                {
                    if (!reloadEventFired)
                        return;
                    else if (!FileWatcher.changedSinceLastGet)
                    {
                        UnityEngine.Debug.LogWarning("没有文件发生改变，不执行热重载");
                        return;
                    }
                }
                finally
                {
                    reloadEventFired = false;
                }
            }

            GenPatcherInputArgsFile();
            ThreadPool.QueueUserWorkItem(RunAssemblyPatchProcess);
        }

        [Serializable]
        public class InputArgs
        {
            public int patchNo;
            public string workDir;
            public string dotnetPath;
            public string cscPath;
            
            public string tempScriptDir;
            public string builtinAssembliesDir;
            public string patchDllPathFormat;
            public string lambdaWrapperBackend;

            public string[] filesChanged;

            public string[] defines;
            public string[] allAssemblyPathes;
        }
        static void GenPatcherInputArgsFile()
        {
            var inputArgs = new InputArgs();
            inputArgs.patchNo = patchNo;
            inputArgs.workDir = Environment.CurrentDirectory.Replace('\\', '/');
            inputArgs.dotnetPath = _dotnetPath;
            inputArgs.cscPath = _cscPath;
            
            inputArgs.tempScriptDir = HotReloadConfig.kTempScriptDir;
            inputArgs.builtinAssembliesDir = HotReloadConfig.kBuiltinAssembliesDir;
            inputArgs.patchDllPathFormat = HotReloadConfig.kPatchDllPathFormat;
            inputArgs.lambdaWrapperBackend = HotReloadConfig.kLambdaWrapperBackend;

            inputArgs.filesChanged = FileWatcher.GetChangedFile();

            inputArgs.defines = EditorUserBuildSettings.activeScriptCompilationDefines;
            inputArgs.allAssemblyPathes = HotReloadUtils.GetAllAssemblyPaths();

            patchDlls.Clear();
            foreach (var fileName in inputArgs.filesChanged)
            {
                var dllName = fileName.Substring(fileName.LastIndexOf(":") + 1);
                if (!patchDlls.Contains(dllName))
                    patchDlls.Add(dllName);
            }

            string jsonStr = JsonUtility.ToJson(inputArgs, true);
            File.WriteAllText(HotReloadConfig.kAssemblyPatcherInput, jsonStr, Encoding.UTF8);
        }

        private static void RunAssemblyPatchProcess(object state)
        {
            _patchState = 200;
            var startInfo = new ProcessStartInfo();
            startInfo.FileName = Path.GetDirectoryName(HotReloadUtils.GetThisFilePath()) + "/AssemblyPatcher~/AssemblyPatcher.exe";
#if PATCHER_DEBUG
            startInfo.Arguments = $"{HotReloadConfig.kAssemblyPatcherInput} {HotReloadConfig.kAssemblyPatcherOutput} debug";
            startInfo.CreateNoWindow = false;
#else
            startInfo.Arguments = string.Format("{0} {1}", HotReloadConfig.kAssemblyPatcherInput, HotReloadConfig.kAssemblyPatcherOutput);
            startInfo.CreateNoWindow = true;
#endif
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            //startInfo.StandardInputEncoding = System.Text.UTF8Encoding.UTF8;
            startInfo.StandardOutputEncoding = System.Text.UTF8Encoding.UTF8;
            startInfo.StandardErrorEncoding = System.Text.UTF8Encoding.UTF8;
            Process procPathcer = new Process();
            procPathcer.StartInfo = startInfo;
            procPathcer.Start();

            Action<StreamReader> outputProcMsgs = sr =>
            {
                string line = sr.ReadLine();
                lock (_patchTaskOutputLock)
                {
                    while (line != null)
                    {
                        _patchTaskOutput.Enqueue(line);
                        line = sr.ReadLine();
                    }
                }
            };

            using (var sr = procPathcer.StandardOutput) { outputProcMsgs(sr); }
            using (var sr = procPathcer.StandardError) { outputProcMsgs(sr); }

            int exitCode = -1;
            if (procPathcer.WaitForExit(60 * 1000)) // 最长等待1分钟
                exitCode = procPathcer.ExitCode;
            else
                procPathcer.Kill();

            if (exitCode == 0)
            {
                try
                {
#if PATCHER_DEBUG
                    MethodHook.onlyShowAddr = true;
#endif
                    // patch dll 生成成功后依次对其执行hook
                    foreach (var dll in patchDlls)
                    {
                        string patchDllPath = string.Format(HotReloadConfig.kPatchDllPathFormat, dll, patchNo);
                        Assembly patchAssembly = Assembly.LoadFrom(patchDllPath);
                        Assembly oriAssembly = null;
                        if (!HotReloadUtils.allAssembliesDic.TryGetValue(dll, out oriAssembly))
                        {
                            throw new Exception(string.Format("can not find assembly with name `{0}`", dll));
                        }

                        HookAssemblies.DoHook(oriAssembly, patchAssembly);
                        //exitCode = -1;
                    }
                }
                catch(Exception ex)
                {
                    HookAssemblies.UnHookDlls(patchDlls);
                    lock (_patchTaskOutputLock)
                    {
                        _patchTaskOutput.Enqueue(string.Format("[Error][ParseOutput] {0}\r\n{1}", ex.Message, ex.StackTrace));
                    }
                    exitCode = -2;
                }
            }
            _patchState = exitCode;
        }

        static void DispatchTaskOutput()
        {
            lock (_patchTaskOutputLock)
            {
                while (_patchTaskOutput.Count > 0)
                {
                    string line = _patchTaskOutput.Dequeue();
                    line = line.Replace("<br/>", "\r\n");
                    if (line.StartsWith("[Info]"))
                        UnityEngine.Debug.Log(string.Format("<color=lime>[Patcher] {0}</color>", line.Substring("[Info]".Length)));
                    else if (line.StartsWith("[Warning]"))
                        UnityEngine.Debug.LogWarning(string.Format("<color=orange>[Patcher] {0}</color>", line.Substring("[Warning]".Length)));
                    else if (line.StartsWith("[Error]"))
                        UnityEngine.Debug.LogError(string.Format("[Patcher] {0}", line.Substring("[Error]".Length)));

#if PATCHER_DEBUG || true
                    else if (line.StartsWith("[Debug]"))
                        UnityEngine.Debug.Log(string.Format("<color=yellow>[Patcher] {0}</color>", line.Substring("[Debug]".Length)));
#endif
                    else
                        UnityEngine.Debug.Log(string.Format("<color=white>[Patcher] {0}</color>", line));
                }
            }
        }

        [Serializable]
        public class OutputReport
        {
            [Serializable]
            public class MethodData
            {
                public string name;
                public string type;
                public string assembly;
                public bool isConstructor;
                public bool isGeneric;
                public bool isPublic;
                public bool isStatic;
                public bool isLambda;
                public string document;
                public string returnType;
                public string[] paramTypes;

                public override string ToString()
                {
                    return name;
                }
            }
            public int patchNo;
            public List<MethodData> methodsNeedHook;
        }

        static Type ParseType(string typeName)
        {
            Type ret = Type.GetType(typeName, true);

            if (ret.ContainsGenericParameters)
            {
                // 我们目标只hook引用类型，值类型每个类型都有不同内存地址，遍历所有类型不划算
                Type[] args = ret.GetGenericArguments();
                for (int i = 0, imax = args.Length; i < imax; i++)
                    args[i] = typeof(object);

                ret = ret.MakeGenericType(args);
            }

            return ret;
        }

    }

}
