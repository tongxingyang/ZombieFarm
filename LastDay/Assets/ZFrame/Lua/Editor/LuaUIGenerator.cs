﻿using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ZFrame.UGUI;

/// <summary>
/// 自动生成Lua脚本处理UI表现和逻辑
/// 1. 每次只能选择一个UI的预设
/// 2. 预设必须挂载一个<LuaComponent>组件
/// 3. LuaComponent组件上定义好目标lua脚本和所需调用的函数
/// 4. UI构建规则
///     以"_"结尾的对象会被忽略
///     前缀      说明
///     lb      该对象挂载有UILable组件  
///     sp      该对象挂载有UISprite组件
///     btn     该对象挂载有UIButton组件
///     tgl     该对象挂载有UIToggle组件
///     bar     该对象挂载有UISlider或UIProgress组件
///     ---     挂载在以上对象下的其他对象会被忽略
///     ent     只能挂载在Grp下面, 该对象下只能挂载以上基本对象
///     Grp     该对象下会必须挂载一个ent前缀，和以上其他对象
///     Sub     该对象下可以挂载所有对象，包括Sub自己
///     elm     只能获取到该节点的GameObject
/// </summary>
public class LuaUIGenerator : EditorWindow
{
    private struct EventSet
    {
        public string path;
        public IEventSender sender;

        public EventSet(string path, IEventSender sender)
        {
            this.path = path; this.sender = sender;
        }
    }

    [MenuItem("Custom/UI脚本生成（Lua)...")]
    public static void ShowWindow()
    {
        EditorWindow edtWnd = EditorWindow.GetWindow(typeof(LuaUIGenerator));
        edtWnd.minSize = new Vector2(800, 650);
        edtWnd.maxSize = edtWnd.minSize;
    }

    const string AUTO_DEFINE_BEGIN = "--!* [开始] 自动生成函数 *--";
    const string AUTO_DEFINE_END = "--!* [结束] 自动生成函数  *--";
    const string AUTO_REGIST = "--!* [结束] 自动生成代码 *--";

    const string INIT_VIEW = "init_view";
    const string INIT_LOGIC = "init_logic";

    private static bool IsDefine(string line, string define)
    {
        return line.Trim() == define;
    }

    private static Dictionary<System.Type, string> s_DArgs = new Dictionary<System.Type, string>() {
        { typeof(UIButton), "btn" },
        { typeof(UIToggle), "tgl" },
        { typeof(UIDropdown), "drp" },
        { typeof(UIEventTrigger), "evt, data" },
        { typeof(UIInputField), "inp, text" },
        { typeof(UIInput), "inp, text" },
        { typeof(UILoopGrid), "go, i" },
        { typeof(UISlider), "bar" },
        { typeof(UIProgress), "bar" },
    };

    private static Dictionary<TriggerType, string> s_DActions = new Dictionary<TriggerType, string>() {
        { TriggerType.PointerEnter, "ptrin" },
        { TriggerType.PointerExit, "ptrout"},
        { TriggerType.PointerDown, "ptrdown"},
        { TriggerType.PointerUp, "ptrup"},
        { TriggerType.PointerClick, "click"},
        { TriggerType.BeginDrag, "begindrag"},
        { TriggerType.Drag, "drag"},
        { TriggerType.EndDrag, "enddrag"},
        { TriggerType.Drop, "drop"},
        { TriggerType.Select, "select"},
        { TriggerType.Deselect, "deselect"},
        { TriggerType.Submit, "submit"},
        { TriggerType.Longpress, "pressed"},
    };

    private static string GetArgs(IEventSender sender)
    {
        foreach (var kv in s_DArgs) {
            if (kv.Key.IsInstanceOfType(sender)) {
                return kv.Value;
            }
        }
        return "sender";
    }

    // 根据控件类型得到回调函数名
    string genFuncName(EventData evt, string path)
    {
        path = path.Replace('/', '_').ToLower();

        string action = null;
        if (s_DActions.TryGetValue(evt.type, out action)) {
            if (evt.name == UIEvent.Auto) {
                if (string.IsNullOrEmpty(path)) {
                    return string.Format("on_{0}", action);
                } else {
                    return string.Format("on_{0}_{1}", path, action);
                }
            }
        }
        return evt.name == UIEvent.Auto ? null : evt.param;
    }

    string opTips = "[---]";
    Vector2 scroll = Vector2.zero;
    string logicFile = "";
    string scriptLogic = "";
    LuaComponent selected;
    bool flagGenCode;
    //
    Dictionary<string, Component> dictRef = new Dictionary<string, Component>();
    private List<EventSet> m_EventSets = new List<EventSet>();

    // *_logic.lua的结构
    string codeDefined = null;
    string codeInited = null;
    Dictionary<string, string> dictFunc = new Dictionary<string, string>();

    public void OnGUI()
    {
        if (GUI.Button(new Rect(0, 0, 100, 20), "生成脚本")) {
            generateWithSelected();
        }
        if (GUI.Button(new Rect(100, 0, 100, 20), "写入脚本")) {
            saveLogic();
        }
        if (GUI.Button(new Rect(200, 0, 100, 20), "生成并写入脚本")) {
            generateWithSelected();
            saveLogic();
        }
        GUI.color = Color.red;
        GUI.Label(new Rect(0, 25, 800, 20), opTips);
        GUI.color = Color.white;

        GUI.BeginGroup(new Rect(0, 50, 800, 650));

        scroll = GUILayout.BeginScrollView(scroll, GUILayout.Width(800), GUILayout.Height(600));

        GUILayout.Label(logicFile);
        if (GUILayout.Button("写入脚本" + logicFile)) {
            saveLogic();
        }
        GUILayout.TextField(scriptLogic);

        GUILayout.EndScrollView();
        GUI.EndGroup();
    }

    void ShowMessage(string str)
    {
        opTips = str;
        //EditorUtility.DisplayDialog("提示", str, "确定");
    }

    // 为选定的预设生成Lua脚本
    private void generateWithSelected()
    {
        GameObject[] goes = Selection.gameObjects;
        if (goes != null && goes.Length == 1) {
            selected = goes[0].GetComponent<LuaComponent>();
            flagGenCode = true;
            if (selected != null) {
                // 清空结构
                codeDefined = null;
                codeInited = null;
                dictRef.Clear();
                m_EventSets.Clear();
                dictFunc.Clear();
                // 生成文件名
                if (string.IsNullOrEmpty(selected.luaScript)) {
                    ShowMessage("脚本名称为空！");
                    return;
                }
                
                string fileName = selected.luaScript;
                if (!fileName.Contains("/")) {
                    if (Directory.Exists(ChunkAPI.GetFilePath("ui/" + fileName))) {
                        // 扩展名为空，名称是模块名
                        selected.luaScript = string.Format("ui/{0}/lc_{1}", fileName, selected.name.ToLower());
                    } else {
                        ShowMessage("不存在的UI模块: " + fileName);
                        return;
                    }
                }

                logicFile = selected.luaScript;
                if (!logicFile.EndsWith(".lua")) logicFile += ".lua";

                parseLogic(logicFile);
                GenerateLogic();
            } else {
                ShowMessage("预设体上需要挂载<LuaComponent>组件");
            }
        } else {
            ShowMessage("只能选择一个预设体来生成脚本");
        }
    }

    // 解析已有的UI Logic脚本
    void parseLogic(string path)
    {
        var filePath = ChunkAPI.GetFilePath(path);
        if (!File.Exists(filePath)) {
            codeDefined = null;
            codeInited = null;
            dictFunc.Clear();
            return;
        }

        // 解析
        string text = File.ReadAllText(filePath);
        // 注释：文件名
        text = text.Substring(text.IndexOf(".lua") + 5);

        var beginIdx = text.IndexOf(AUTO_DEFINE_BEGIN);
        int codeStart = beginIdx - 1;
        if (codeStart > 0) {
            codeDefined = text.Substring(0, codeStart);
        } else {
            codeDefined = null;
        }

        codeStart = beginIdx;
        if (codeStart >= 0) {
            text = text.Substring(codeStart);
        } else {
            return;
        }
        string funcName = null;
        string funcDefine = null;
        using (var reader = new StringReader(text)) {
            for (;;) {
                var line = reader.ReadLine();
                if (line == null) break;

                if (IsDefine(line, AUTO_DEFINE_END)) continue;

                if (IsDefine(line, AUTO_REGIST)) {
                    if (funcName == INIT_VIEW) {
                        // 开始记录codeInited
                        codeInited = "";
                    }
                    continue;
                } else if (line.Contains("return self")) {
                    if (funcName != null) {
                        dictFunc.Add(funcName, funcDefine.Substring(0, funcDefine.Length - 1));
                    }
                    break;
                }
                // function inside function will be ignore...
                string[] segs = line.Split(new char[] { ' ', '\t', '\n' }, System.StringSplitOptions.None);
                if (segs != null && segs.Length > 0 && segs[0] == "function") {
                    if (funcName != null) {
                        if (funcName != INIT_VIEW) {
                            try {
                                dictFunc.Add(funcName, funcDefine.Substring(0, funcDefine.Length - 1));
                            } catch (System.Exception e) {
                                Debug.LogError(e.Message + ":" + funcName);
                            }
                        } else {
                            int endIndex = codeInited.LastIndexOf("end");
                            codeInited = codeInited.Substring(0, endIndex);
                        }
                    }
                    // 取函数名, 记录函数
                    funcName = segs[1].Substring(0, segs[1].IndexOf('(')).Trim();
                    funcDefine = '\n' + line + '\n';
                } else {
                    if (funcName != null) {
                        if (funcName == INIT_VIEW && codeInited != null) {
                            codeInited += line + '\n';
                        }
                        funcDefine += line + '\n';
                    }
                }
            }
        }
    }

    // 生成UI Logic脚本 
    private void GenerateLogic()
    {
        StoreGrpEnts(selected.transform, selected.transform);
        
        StoreSelectable();

        // 文件头
        var strbld = new StringBuilder();
        genFileHeader(strbld);

        // 独立的Triggler
        GenSingleTrigger(strbld);

        // 自定义的事件处理函数
        List<string> listOnMethods = new List<string>();
        foreach (KeyValuePair<string, string> kv in dictFunc) {
            if (kv.Key == INIT_VIEW || kv.Key == INIT_LOGIC) continue;
            if (selected.LocalMethods.Contains(kv.Key)) continue;

            strbld.Append(kv.Value);
            listOnMethods.Add(kv.Key);
        }
        foreach (string method in listOnMethods) {
            dictFunc.Remove(method);
        }
        listOnMethods.Clear();

        // 界面显示初始化
        functionBegin(strbld, false, INIT_VIEW);

        // Grp生成方法
        MakeGroupFunc(strbld);

        normal(strbld, AUTO_REGIST);
        strbld.Append(codeInited);
        functionEnd(strbld);

        // 界面逻辑初始化
        generateFunc(strbld, INIT_LOGIC);
        
        // 其他函数
        foreach (KeyValuePair<string, string> kv in dictFunc) {
            strbld.Append(kv.Value);
        }

        // 预设函数
        foreach (var method in selected.LocalMethods) {
            if (!dictFunc.ContainsKey(method)) {
                generateFunc(strbld, method);
            }
        }

        // return表
        normal(strbld, "\nreturn self\n");

        scriptLogic = strbld.ToString();
    }

    /// <summary>
    /// 记录所有Selectable
    /// </summary>
    private void StoreSelectable()
    {
        var coms = selected.GetComponentsInChildren(typeof(IEventSender), true);
        foreach (var com in coms) {
            var sender = (IEventSender)com;
            bool hasSendEvent = false;
            foreach (var evt in sender) {
                if (evt.name == UIEvent.Auto || evt.name == UIEvent.Send) {
                    hasSendEvent = true;
                    break;
                }
            }
            if (!hasSendEvent) continue;

            var path = com.GetHierarchy(selected.transform);
            if (!string.IsNullOrEmpty(path)) {
                var c = path[path.Length - 1];
                if (c == '_' || c == '=') continue;
                if (path.Contains("Elm")) continue;
            }
            
            //dictRef.AddOrReplace(path, com);
            m_EventSets.Add(new EventSet(path, sender));
        }
    }

    void genFileTmpl(StringBuilder strbld)
    {
        var data = System.DateTime.Now;
        strbld.AppendFormat("-- @author  {0}\n", System.Environment.UserName);
        strbld.AppendFormat("-- @date    {0}\n", data.ToString("yyyy-MM-dd HH:mm:ss"));
        strbld.AppendFormat("-- @desc    {0}\n", selected.name);
        strbld.AppendLine("--\n");
    }

    // 生成文件头
    void genFileHeader(StringBuilder strbld)
    {
        // 注释：文件名
        strbld.AppendFormat("--\n-- @file    {0}\n", logicFile);

        // 已有的本地变量定
        if (codeDefined != null) {
            codeDefined = codeDefined.TrimStart();
            if (!codeDefined.StartsWith("-- @author")) {
                genFileTmpl(strbld);
            }
            normal(strbld, codeDefined);
        } else {
            genFileTmpl(strbld);

            normal(strbld, "local self = ui.new()");
            //normal(strbld, "setfenv(1, self)");
            // Lua5.2以后
            normal(strbld, "local _ENV = self");
        }
    }

    /// <summary>
    ///  独立的触发器，对于ent中的触发器不生成，仅记录下来
    /// </summary>
    void GenSingleTrigger(StringBuilder strbld)
    {
        normal(strbld, AUTO_DEFINE_BEGIN);

        var listFuncs = new List<string>();
        foreach (var set in m_EventSets) {
            var path = set.path;
            var Events = set.sender;
            if (Events != null) {
                foreach (var Event in Events) {
                    var funcName = genFuncName(Event, path);
                    if (!string.IsNullOrEmpty(funcName)) {
                        if (listFuncs.Contains(funcName)) continue;
                        listFuncs.Add(funcName);
                        Event.param = funcName;
                        var args = GetArgs(Events);
                        if (string.IsNullOrEmpty(args)) {
                            generateFunc(strbld, funcName);
                        } else {
                            generateFunc(strbld, funcName, args);
                        }
                    }
                }
            }
        }

        normal(strbld, AUTO_DEFINE_END);
    }

    /// <summary>
    /// 产生Group生产的代码
    /// </summary>
    private void MakeGroupFunc(StringBuilder strbld)
    {
        foreach (KeyValuePair<string, Component> kv in dictRef) {
            string path = kv.Key;                        
            string entName = Path.GetFileNameWithoutExtension(path);
            if (!entName.StartsWith("ent")) continue;

            string grpName = SystemTools.GetDirPath(path);
            string pointName = grpName.Replace('/', '.');
            normal(strbld, string.Format("ui.group(Ref.{0})", pointName));
        }
    }

    // 生成回调函数定义
    void generateFunc(StringBuilder strbld, string funcName, params string[] args)
    {
        string content;
        if (dictFunc.TryGetValue(funcName, out content)) {
            strbld.Append(content);
            dictFunc.Remove(funcName);
        } else {
            functionBegin(strbld, false, funcName, args);
            normal(strbld, "");
            functionEnd(strbld);
        }
    }
    
    void saveLogic()
    {
        if (!string.IsNullOrEmpty(scriptLogic)) {
            string path = ChunkAPI.GetFilePath(logicFile);
            File.WriteAllText(path, scriptLogic);
            ShowMessage(string.Format("写入{0}成功！", path));

            var selectedObj = selected.gameObject;
            var type = PrefabUtility.GetPrefabType(selectedObj);
            string prefabPath = null;
            Object prefab = null;
            if (type == PrefabType.None) {
                prefabPath = string.Format("Assets/{0}/BUNDLE/UI/{1}.prefab",
                    ZFrame.Asset.AssetBundleLoader.DIR_ASSETS, selectedObj.name);
                prefab = AssetDatabase.LoadAssetAtPath(prefabPath, typeof(GameObject));
            } else {
                prefab = PrefabUtility.GetCorrespondingObjectFromSource(selectedObj);
                prefabPath = AssetDatabase.GetAssetPath(prefab);
            }
            if (!prefab) {
                PrefabUtility.CreatePrefab(prefabPath, selectedObj, ReplacePrefabOptions.ConnectToPrefab);
                AssetDatabase.Refresh();
                var ai = AssetImporter.GetAtPath(prefabPath);
                ai.assetBundleName = "ui";
            } else {
                PrefabUtility.ReplacePrefab(selectedObj, prefab, ReplacePrefabOptions.ConnectToPrefab);
            }
        } else {
            ShowMessage(logicFile + "脚本为空!");
        }
    }

    /// <summary>
    /// 解析窗口结构，记录所有组
    /// </summary>
    private void StoreGrpEnts(Transform root, Transform curr)
    {
        string findPreffix = curr.GetHierarchy(root);

        for (int i = 0; i < curr.childCount; ++i) {
            Transform trans = curr.GetChild(i);
            if (!trans.gameObject.activeSelf) continue;

            string sName = trans.name;
            if (sName.EndsWith('_')) continue;

            if (sName.StartsWith("Sub")) {
                StoreGrpEnts(root, trans);
            } else if (sName.StartsWith("Grp")) {
                StoreGrpEnts(root, trans);
            } else if (sName.StartsWith("ent")) {
                dictRef.Add(findPreffix + "/" + sName, trans);
            }
        }
    }

    #region 以下为Lua代码组装

    int step = 0;
    int nt = 0;
    StringBuilder appendTabs(StringBuilder strbld)
    {
        if (!flagGenCode) return strbld;

        for (int i = 0; i < step; ++i) {
            strbld.Append("\t");
        }
        return strbld;
    }

    void functionBegin(StringBuilder strbld, bool blocal, string funcName, params string[] Params)
    {
        if (!flagGenCode) return;

        strbld.Append('\n');
        appendTabs(strbld);
        strbld.AppendFormat("{0}function {1}", blocal ? "local " : "", funcName);
        if (Params != null && Params.Length > 0) {
            strbld.Append('(');
            for (int i = 0; i < Params.Length; ++i) {
                strbld.Append(Params[i]);
                if (i < Params.Length - 1) {
                    strbld.Append(", ");
                } else if (i == Params.Length - 1) {
                    strbld.Append(")\n");
                }
            }
        } else {
            strbld.Append("()\n");
        }
        step += 1;
    }
    void functionEnd(StringBuilder strbld)
    {
        if (!flagGenCode) return;

        step -= 1;
        appendTabs(strbld);
        strbld.Append("end\n");
    }

    void ifBegin(StringBuilder strbld, string logic)
    {
        if (!flagGenCode) return;

        appendTabs(strbld);
        strbld.AppendFormat("if {0} then\n", logic);
        step += 1;
    }
    void ifEnd(StringBuilder strbld)
    {
        if (!flagGenCode) return;

        step -= 1;
        appendTabs(strbld);
        strbld.Append("end\n");
    }
    void forBegin(StringBuilder strbld, string var, string from, string to)
    {
        if (!flagGenCode) return;

        appendTabs(strbld);
        strbld.AppendFormat("for {0} = {1}, {2} do\n", var, from, to);
        step += 1;
    }
    void forEnd(StringBuilder strbld)
    {
        if (!flagGenCode) return;

        step -= 1;
        appendTabs(strbld);
        strbld.Append("end\n");
    }

    void tableBegin(StringBuilder strbld, string tableName)
    {
        if (!flagGenCode) return;

        appendTabs(strbld);
        if (string.IsNullOrEmpty(tableName)) {
            strbld.Append("{\n");
        } else {
            strbld.AppendFormat("{0} = {1}\n", tableName, '{');
        }
        step += 1;
        nt += 1;
    }
    void tableEnd(StringBuilder strbld)
    {
        if (!flagGenCode) return;

        step -= 1;
        nt -= 1;
        appendTabs(strbld).Append('}');
        if (nt > 0) {
            strbld.Append(',');
        }
        strbld.Append("\n");
    }
    void normal(StringBuilder strbld, string logic)
    {
        if (!flagGenCode) return;

        if (logic != null) {
            appendTabs(strbld).Append(logic);
            if (nt > 0) {
                strbld.Append(',');
            }
            strbld.Append("\n");
        }
    }
    #endregion
}