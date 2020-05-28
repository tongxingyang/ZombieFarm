﻿using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ZFrame.Asset;
using ZFrame.Platform;
#if ULUA
using LuaInterface;
#else
using XLua;
using LuaCSFunction = XLua.LuaDLL.lua_CSFunction;
using LuaState = XLua.LuaEnv;
#endif
using ILuaState = System.IntPtr;

public static class LibAsset
{
    private class AssetProgress : LuaFunction, IAssetProgress
    {
        private object m_Param;
        public AssetProgress(int reference, LuaState l, object param) : base(reference, l)
        {
            m_Param = param;
        }

        public void SetProgress(float progress)
        {
            if (progress >= 1) {
                Action(m_Param);
                var disposable = m_Param as System.IDisposable;
                if (disposable != null) disposable.Dispose();
            }
        }

        void IAssetProgress.OnBundleLoaded(string bundleName, AbstractAssetBundleRef bundle) { }
    }

    public const string LIB_NAME = "libasset.cs";

    [MonoPInvokeCallback(typeof(LuaCSFunction))]
    public static int OpenLib(ILuaState lua)
    {
        lua.NewTable();
        lua.SetDict("GetVersion", GetVersion);
        lua.SetDict("GetResHeight", GetResHeight);
        lua.SetDict("UpdateLua", UpdateLua);
        lua.SetDict("Get", Get);

        // Asset Methods
        lua.SetDict("AutoPool", AutoPool);
        lua.SetDict("Preload", Preload);
        lua.SetDict("Load", Load);
        lua.SetDict("LoadAsync", LoadAsync);
        lua.SetDict("Unload", Unload);
        lua.SetDict("StopLoading", StopLoading);
        lua.SetDict("ViewLoading", ViewLoading);
        lua.SetDict("LimitAsset", LimitAssetBundle);

        lua.SetDict("PrepareAssets", PrepareAssets);

        lua.SetDict("UpdateBundleList", UpdateBundleList);
        lua.SetDict("Unpack", Unpack);
        return 1;
    }

    public static void LuaOnLoaded(ILuaState lua, int index)
    {
        var oldTop = lua.GetTop();
        var nArgs = oldTop - index;

        //lua.PushCSharpFunction(LuaStatic.traceback);
        lua.PushErrorFunc();
        var b = oldTop + 1;

        lua.PushValue(index++);

        for (int i = 0; i < nArgs; ++i) {
            lua.PushValue(index + i);
        }

        if (lua.PCall(nArgs, 0, b) == LuaThreadStatus.LUA_OK) {
            lua.Remove(b);
        } else {
            string err = lua.ToString(-1);
            lua.SetTop(oldTop);

            if (err == null) err = "Unknown Lua Error";
            LogMgr.E(err);
        }
    }

    [MonoPInvokeCallback(typeof(LuaCSFunction))]
    private static int GetVersion(ILuaState lua)
    {
        lua.CreateTable(0, 3);
        lua.SetDict("version", VersionMgr.AppVersion.version);
        lua.SetDict("timeCreated", VersionMgr.AppVersion.timeCreated);
        lua.SetDict("whoCreated", VersionMgr.AppVersion.whoCreated);

        lua.CreateTable(0, 3);
        lua.SetDict("version", VersionMgr.AssetVersion.version);
        lua.SetDict("timeCreated", VersionMgr.AssetVersion.timeCreated);
        lua.SetDict("whoCreated", VersionMgr.AssetVersion.whoCreated);

        return 2;
    }

    [MonoPInvokeCallback(typeof(LuaCSFunction))]
    private static int GetResHeight(ILuaState lua)
    {
        lua.PushInteger(AssetsMgr.Instance.resHeight);
        return 1;
    }

    [MonoPInvokeCallback(typeof(LuaCSFunction))]
    private static int UpdateLua(ILuaState lua)
    {
        string usingMD5 = lua.ChkString(1);
        string usingDate = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        PlayerPrefs.SetString("Using-Lua", usingMD5);
        PlayerPrefs.SetString("Using-Lua-Date", usingDate);
        return 0;
    }

    [MonoPInvokeCallback(typeof(LuaCSFunction))]
    private static int Get(ILuaState lua)
    {
        string path = lua.ChkString(1);
        string name = lua.ChkString(2);
        object type = lua.ToUserData(3);
        var lib = ObjectLibrary.Load(path);
        if (lib) {
            var obj = lib.Get(name, type as System.Type);
            if (obj) {
                lua.PushLightUserData(obj);
            } else {
                lua.PushNil();
            }
        }

        return 1;
    }

    [MonoPInvokeCallback(typeof(LuaCSFunction))]
    private static int AutoPool(ILuaState lua)
    {
        var go = lua.ToGameObject(1);
        if (go) {
            go.NeedComponent<AutoPooled>();
        }
        return 0;
    }

    [MonoPInvokeCallback(typeof(LuaCSFunction))]
    private static int Preload(ILuaState lua)
    {
        var loader = AssetsMgr.A.Loader;
        var tasks = new List<AsyncLoadingTask>();
        var type = lua.Type(1);
        if (type == LuaTypes.LUA_TSTRING) {
            var prelads = AssetsMgr.A.Load(typeof(PreloadAssets), lua.ToString(1)) as PreloadAssets;
            if (prelads != null) {
                foreach (var asset in prelads.assets) {
                    if (!loader.IsLoaded(asset.path)) {
                        tasks.Add(loader.NewTask(asset.path, asset.method));
                    }
                }
            }
        } else if (type == LuaTypes.LUA_TTABLE) {
            lua.PushNil();
            while (lua.Next(1)) {
                string assetPath = null;
                LoadMethod method = LoadMethod.Default;
                switch (lua.Type(-1)) {
                    case LuaTypes.LUA_TSTRING:
                        assetPath = lua.ToString(-1);
                        break;
                    case LuaTypes.LUA_TTABLE:
                        assetPath = lua.GetString(-1, "path");
                        method = (LoadMethod)lua.GetEnum(-1, "method", method);
                        break;
                }
                lua.Pop(1);

                if (!string.IsNullOrEmpty(assetPath) && !loader.IsLoaded(assetPath)) {
                    tasks.Add(loader.NewTask(assetPath, method));
                }
            }
        }
        
        if (tasks.Count > 0) {
            IAssetProgress progress = null;
            if (lua.IsFunction(2)) {
                lua.PushValue(2);
                var @ref = lua.L_Ref(LuaIndexes.LUA_REGISTRYINDEX);
                object param = lua.ToAnyObject(3);
                var env = ObjectTranslatorPool.Instance.Find(lua).luaEnv;
                progress = new AssetProgress(@ref, env, param);
            }
            loader.StartCoroutine(loader.LoadMultiBundles(tasks, progress));
        } else {
            // 没有需要加载的资源，直接执行回调函数
            if (lua.IsFunction(2)) {
                LuaOnLoaded(lua, 2);
            }
        }

        return 0;
    }

    [MonoPInvokeCallback(typeof(LuaCSFunction))]
    private static int Load(ILuaState lua)
    {
        var type = lua.ToUserData(1) as System.Type;
        string path = lua.ChkString(2);
        bool warnIfMissing = lua.OptBoolean(3, true);
        lua.PushLightUserData(AssetsMgr.A.Load(type, path, warnIfMissing));
        return 1;
    }

    [MonoPInvokeCallback(typeof(LuaCSFunction))]
    private static int LoadAsync(ILuaState lua)
    {
        var loader = AssetsMgr.A.Loader;
        object obj = lua.ToUserData(1);
        string path = lua.ChkString(2);
        System.Type type = obj as System.Type;
        Object asset;
        var loaded = loader.TryLoad(type, path, out asset);
        if (!loaded) {
            LoadMethod method = LoadMethod.Default;
            if (lua.IsNoneOrNil(3)) {
                method = LoadMethod.Cache;
            } else if (lua.IsBoolean(3)) {
                method = lua.ToBoolean(3) ? LoadMethod.Cache : LoadMethod.Forever;
            } else { 
                method = (LoadMethod)lua.OptEnumValue(3, typeof(LoadMethod), LoadMethod.Default);
            }
            var funcRef = lua.ToLuaFunction(4);
            object param = lua.ToAnyObject(5);
            DelegateObjectLoaded onLoaded = null;
            if (funcRef != null) {
                onLoaded = (a, o, p) => {
                    var L = funcRef.GetState();
                    funcRef.push(L);
                    var b = L.BeginPCall();
                    L.PushString(a);
                    L.PushLightUserData(o);
                    L.PushAnyObject(p);
                    L.ExecPCall(3, 0, b);
                    funcRef.Dispose();
                    var disposer = p as System.IDisposable;
                    if (disposer != null) disposer.Dispose();
                };
            }
            loader.LoadAsync(type, path, method, onLoaded, param);
        } else {
            if (lua.IsFunction(4)) {
                var param = !lua.IsNone(5);
                lua.PushValue(2);
                lua.PushLightUserData(asset);
                if (param) {
                    lua.PushValue(5);
                    lua.Remove(5);
                }
                LuaOnLoaded(lua, 4);
            }
        }
        return 0;
    }

    [MonoPInvokeCallback(typeof(LuaCSFunction))]
    private static int Unload(ILuaState lua)
    {
        string asstPath = lua.OptString(1, null);
        if (string.IsNullOrEmpty(asstPath)) {
            AssetsMgr.A.Loader.UnloadAll();
        } else {
            AssetsMgr.A.Loader.Unload(asstPath, lua.OptBoolean(2, false));
        }
        return 0;
    }

    [MonoPInvokeCallback(typeof(LuaCSFunction))]
    private static int StopLoading(ILuaState lua)
    {
        var param = lua.OptString(1, null);
        AssetsMgr.A.Loader.StopLoading(param);
        return 0;
    }

    [MonoPInvokeCallback(typeof(LuaCSFunction))]
    private static int ViewLoading(ILuaState lua)
    {
        var func = lua.ToLuaFunction(1);
        if (func != null) {
            AssetsMgr.A.Loader.Loading = (bundle, f) => {
                var top = func.BeginPCall();
                var L = func.GetState();
                L.PushString(bundle);
                L.PushNumber(f);
                func.PCall(top, 2);
                func.EndPCall(top);
            };
        } else {
            AssetsMgr.A.Loader.Loading = null;
        }
        return 0;
    }

    [MonoPInvokeCallback(typeof(LuaCSFunction))]
    private static int LimitAssetBundle(ILuaState lua)
    {
        string group = lua.ChkString(1);
        int limit = lua.ChkInteger(2);
        var nUnload = AssetsMgr.A.Loader.LimitAssetBundle(group, limit);
        if (nUnload > 0) {
            LogMgr.D(lua.DebugCurrentLine(2) + "LimitAssetBundle = {0}", nUnload);
        }
        return 0;
    }

    [MonoPInvokeCallback(typeof(LuaCSFunction))]
    private static int PrepareAssets(ILuaState lua)
    {
        var Loader = AssetsMgr.A.Loader;
        Loader.ClearPreload();
        if (lua.IsTable(1)) {
            lua.PushNil();
            while (lua.Next(1)) {
                var path = lua.GetString(-1, "path");
                var method = (LoadMethod)lua.GetEnum(-1, "method", LoadMethod.Default);
                Loader.CachedPreload(path, method);
                lua.Pop(1);
            }
            if (lua.OptBoolean(2, false)) {
                Loader.StartCoroutine(Loader.PreloadingBundles(null));
            }
        }
        return 0;
    }

    private static IEnumerator CoroUnpacking(string url, string dest, int block, LuaFunction func)
    {
        var unpacker = new ZFrame.Bundle.Unpacker(block);
        unpacker.Start(url, dest);
        string name, md5;
        int siz;
        for (;;) {
            yield return null;
            var progress = unpacker.progress;
            var exception = unpacker.exception;

            if (func != null) {
                var top = func.BeginPCall();
                var L = func.GetState();
                L.PushNumber(progress);
                if (exception == null) {
                    L.NewTable();
                    var i = 1;
                    while (unpacker.PeekUnpacking(out name, out md5, out siz)) {
                        L.PushInteger(i++);
                        L.CreateTable(0, 3);
                        L.SetDict("name", name);
                        L.SetDict("md5", md5);
                        L.SetDict("siz", siz);
                        L.SetTable(-3);
                    }
                } else {
                    if (exception is System.IO.IOException) {
                        L.PushString("IOException: " + exception.Message);
                    } else {
                        L.PushString(exception.Message);
                    }
                }
                func.PCall(top, 2);
            }
            
            if (progress < 0 || progress == 1) break;
        }

        func.Dispose();
    }

    [MonoPInvokeCallback(typeof(LuaCSFunction))]
    private static int UpdateBundleList(ILuaState lua)
    {
        if (AssetBundleLoader.I && lua.IsTable(1)) {
            AssetBundleLoader.I.allAssetBundles.Clear();
            lua.PushNil();
            while (lua.Next(1)) {
                AssetBundleLoader.I.allAssetBundles.Add(lua.ToString(-2));
                lua.Pop(1);
            }
        }
        return 0;
    }

    [MonoPInvokeCallback(typeof(LuaCSFunction))]
    private static int Unpack(ILuaState lua)
    {
        var url = lua.ToString(1);
        if (System.IO.File.Exists(url)) {
            var dest = lua.ToString(2);
            var blockSiz = lua.ToInteger(3);
            var func = lua.ToLuaFunction(4);
            AssetsMgr.A.StartCoroutine(CoroUnpacking(url, dest, blockSiz, func));
            lua.PushBoolean(true);
        } else {
            lua.PushBoolean(false);
        }

        return 1;
    }
}