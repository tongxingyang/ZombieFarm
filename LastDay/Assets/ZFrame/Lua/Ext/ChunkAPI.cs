﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using XLua;
using ILuaState = System.IntPtr;

public static class ChunkAPI  
{
    public static string LuaROOT = string.Format("{0}/Essets/LuaRoot",
          System.IO.Path.GetDirectoryName(Application.dataPath.Replace("\\", "/")));

    public static string GetFilePath(string file)
    {
        return string.IsNullOrEmpty(file) ? LuaROOT : string.Format("{0}/{1}", LuaROOT, file);
    }

    public static byte[] __Loader(ref string file)
    {
#if UNITY_EDITOR
        if (AssetsMgr.A && AssetsMgr.A.printLoadedLuaStack) {
            LogMgr.D("Lua Loading: {0}", file);
            using (var wr = System.IO.File.AppendText("lualoading.tmp")) {
                wr.WriteLine(file);
            }

        }
#endif
        byte[] nbytes = null;
        if (AssetsMgr.A && AssetsMgr.A.useLuaAssetBundle) {
            string assetbundleName = file.OrdinalStartsWith("config") ? "lua/config" : "lua/script";
            string assetName = file.Replace('/', '%');
            var txtAsset = AssetsMgr.A.Load<TextAsset>(assetbundleName + "/" + assetName, false);
            if (txtAsset == null) return null;

            nbytes = txtAsset.bytes;
            CLZF2.Decrypt(nbytes, nbytes.Length);
            nbytes = CLZF2.DllDecompress(nbytes);
        } else {
            if (!file.OrdinalEndsWith(".lua")) file = file + ".lua";
            var luaPath = GetFilePath(file);
            if (!System.IO.File.Exists(luaPath)) return null;

            nbytes = System.IO.File.ReadAllBytes(luaPath);
        }

        if (nbytes[0] == 0xEF && nbytes[1] == 0xBB && nbytes[2] == 0xBF) {
            // 去掉BOM头
            System.Array.Copy(nbytes, 3, nbytes, 0, nbytes.Length - 3);
        }

        return nbytes;
    }

    private static byte[] LoadBytes(this ILuaState L, string fileName)
    {
        string lowerName = fileName.ToLower();
        if (lowerName.OrdinalEndsWith(".lua")) {
            int index = fileName.LastIndexOf('.');
            fileName = fileName.Substring(0, index);
        }
        fileName = fileName.Replace('.', '/');

        // Load with Unity3D resources
        return __Loader(ref fileName);
    }

    public static LuaThreadStatus LoadFile(this ILuaState L, string fileName)
    {
        var bytes = L.LoadBytes(fileName);
        if (bytes != null) {
            return L.L_LoadBuffer(bytes, fileName);
        }

        return LuaThreadStatus.LUA_ERRERR;
    }

    public static int DoFile(this ILuaState L, string fileName)
    {
        var bytes = L.LoadBytes(fileName);
        if (bytes != null) {
            var oldTop = L.GetTop();
            L.PushErrorFunc();
            var b = oldTop + 1;
            if (L.L_LoadBuffer(bytes, fileName) == LuaThreadStatus.LUA_OK) {
                L.PushString(fileName);
                if (L.PCall(1, -1, b) == 0) {
                    L.Remove(b);
                    return L.GetTop() - oldTop;
                }
            }
            L.ToTranslator().luaEnv.ThrowExceptionFromError(oldTop);
        }
        return 0;
    }

    public static int DoBuffer(this ILuaState L, byte[] nbytes, string name)
    {
        int oldTop = L.GetTop();
        L.PushErrorFunc();
        var b = oldTop + 1;

        if (L.L_LoadBuffer(nbytes, name) == LuaThreadStatus.LUA_OK
            && L.PCall(0, -1, b) == LuaThreadStatus.LUA_OK) {
            L.Remove(b);
            return L.GetTop() - oldTop;
        }

        L.ToTranslator().luaEnv.ThrowExceptionFromError(oldTop);
        return 0;
    }

    public static int RefChunk(this ILuaState self, string chunk)
    {
        self.L_DoString(chunk);
        return self.L_Ref(LuaIndexes.LUA_REGISTRYINDEX);
    }

    public static void RefChunk(this ILuaState self, string key, string chunk)
    {
        self.PushString(key);
        self.L_DoString(chunk);
        self.RawSet(LuaIndexes.LUA_REGISTRYINDEX);
    }
}