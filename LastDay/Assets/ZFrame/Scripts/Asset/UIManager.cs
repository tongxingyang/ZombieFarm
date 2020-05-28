﻿using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.UI;
using UnityEngine.U2D;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using UnityEngine.EventSystems;

namespace ZFrame
{
    using UGUI;

    public class UIManager : MonoSingleton<UIManager>
    {
        private const int MAX_DEPTH = 99;
        [SerializeField] private int m_ResHeight = 720;

        [SerializeField] private Vector3 m_Origin;

        [SerializeField] private Canvas[] m_Canvases;
        [SerializeField] private KeyCode[] m_Keys;
        public event System.Action<KeyCode> onKey;

#if UNITY_EDITOR
        private float m_fScreenHeight = Screen.height;
        private float m_fScreenWidth = Screen.width;
#endif
        private DeviceOrientation m_CurScreenOrientation;
        public event System.Action<DeviceOrientation> onScreenOrientationChanged;
        
        public void UpdateCanvasScale(int width, int height)
        {
            var aspectRadio = (float)width / height;
            foreach (var cv in m_Canvases) {
                var cvScale = cv.GetComponent(typeof(CanvasScaler)) as CanvasScaler;
                if (cvScale) {
                    var scale = cvScale.referenceResolution.x / cvScale.referenceResolution.y;
                    cvScale.matchWidthOrHeight = scale > aspectRadio ? 0 : 1;
                }
            }
        }

        public Canvas GetCanvas(int index)
        {
            if (index < m_Canvases.Length) {
                return m_Canvases[index];
            }

            return null;
        }

        private KeyCode GetKeyDown()
        {
            for (int i = 0; i < m_Keys.Length; ++i) {
                var key = m_Keys[i];
                if (Input.GetKeyDown(key)) {
                    return key;
                }
            }

            return KeyCode.None;
        }

        private GameObject CreateNewWindow(GameObject prefab, Transform canvasTrans, int sibling, int depth)
        {
            var go = ObjectPoolManager.AddChild(canvasTrans.gameObject, prefab, sibling);
            if (!go.activeSelf) go.SetActive(true);
            var wnd = go.GetComponent(typeof(UIWindow)) as UIWindow;
            if (wnd) {
                wnd.depth = depth;
            }
            var fitSafeScreen = go.GetComponent(typeof(UIFitSafeScreen)) as UIFitSafeScreen;
            if (fitSafeScreen == null)
            {
                fitSafeScreen = go.AddComponent(typeof(UIFitSafeScreen)) as UIFitSafeScreen;
            }

            return go;
        }

        private IEnumerator CreateWindow(UIWindow origin, Transform canvasTrans, int depth)
        {
            foreach (var asset in origin.preloadAssets) {
                yield return AssetsMgr.A.Loader.LoadingAsset(null, asset, Asset.LoadMethod.Default);
            }

            var sibling = CalcSiblingIndex(null, canvasTrans, depth);
            CreateNewWindow(origin.gameObject, canvasTrans, sibling, depth);
        }

        public GameObject CreateWindow(GameObject prefab, int depth = 0, int canvasId = 0)
        {
            Assert.IsTrue(prefab, "窗口预设为空！");

            var canvas = GetCanvas(canvasId) ?? m_Canvases[0];
            var canvasTransform = canvas.transform;

            // 界面是否已经是可见
            var wndName = Path.GetFileName(prefab.name);
            UIWindow lc = UIWindow.FindByName(wndName);
            if (lc) {
                var siblingIndex = CalcSiblingIndex(lc, canvasTransform, depth);
                lc.depth = depth;
                lc.transform.SetSiblingIndex(siblingIndex);
                return lc.gameObject;
            }

            // 新初始化界面                
            var wnd = prefab.GetComponent(typeof(UIWindow)) as UIWindow;
            if (wnd && wnd.preloadAssets != null && wnd.preloadAssets.Length > 0) {
                foreach (var asset in wnd.preloadAssets) {
                    if (!AssetsMgr.A.Loader.IsLoaded(asset)) {
                        // 先预加载资源，不会立即返回窗口对象
                        StartCoroutine(CreateWindow(wnd, canvasTransform, depth));
                        return null;
                    }
                }
            }

            var sibling = CalcSiblingIndex(null, canvasTransform, depth);
            return CreateNewWindow(prefab, canvasTransform, sibling, depth);
        }

        /// <summary>
        /// 创建一个UI窗口
        /// </summary>
        /// <param name="prefabName">窗口预设路径</param>
        /// <param name="depth">深度：高的出现在前面；同样深度，后创建的出现在前面；0表示自动最前面</param>
        /// <param name="canvasId">画布：窗口创建后，挂载在哪个画布下</param>
        /// <returns></returns>
        public GameObject CreateWindow(string prefabName, int depth = 0, int canvasId = 0)
        {
            var prefab = AssetsMgr.A.Load<GameObject>(prefabName);
            return prefab ? CreateWindow(prefab, depth, canvasId) : null;
        }

        public int GetTopDepth(int canvasId = 0)
        {
            var trans = GetCanvas(canvasId).transform;
            var wnd = trans.GetChild(trans.childCount - 1).GetComponent<UIWindow>();
            return wnd != null ? wnd.depth : 0;
        }

        protected override void Awaking()
        {
            base.Awaking();

            // 测试IphoneX
            Rect safeArea = Screen.safeArea;
            //safeArea = new Rect(132, 0, 2172, 1125);
            UIFitSafeScreen.SetSafeArea(safeArea);

            SpriteAtlasManager.atlasRequested += (tag, action) => {
                var atlas = AssetsMgr.A.Load(typeof(SpriteAtlas), string.Format("Atlas/{0}/{0}", tag));
                action.Invoke(atlas as SpriteAtlas);
            };

            // UpdateCanvasScale(Screen.width, Screen.height);
            // AssetsMgr.A.onResolutionChanged += UpdateCanvasScale;
        }

        // Use this for initialization
        private void Start()
        {
            m_CurScreenOrientation = Input.deviceOrientation;

            transform.position = m_Origin;

            Tween.ZTween.Init();

            CLZF2.Decrypt(null, AssetsMgr.VER + m_ResHeight);
            LuaScriptMgr.OnScriptsFinish();
#if XWZ_DEBUG || UNITY_EDITOR || UNITY_STANDALONE
            m_WndRc = new Rect(0, 30, 200, 200);
            //编辑器工具管理类
            //LogMgr.D("加载编辑器工具...");
            //gameObject.AddComponent<GETools.GETBoot>();
#endif
#if UNITY_EDITOR || SHOW_FPS
            gameObject.AddComponent(typeof(FrameRateCounter));
#endif
        }
        
        private System.Action m_OnDrawGui;
        private bool m_ShowWindow;
        private Rect m_WndRc;
        private Vector2 m_ScrPos;

        private static readonly GUI.WindowFunction DrawWindow = (id) => {
            Instance.m_ScrPos = GUILayout.BeginScrollView(Instance.m_ScrPos);
            Instance.m_OnDrawGui.Invoke();
            GUILayout.EndScrollView();

            GUI.DragWindow();
        };

        [Conditional(LogMgr.DEBUG), Conditional("UNITY_EDITOR"), Conditional("UNITY_STANDALONE")]
        public void RegDrawGUI(System.Action action)
        {
            m_OnDrawGui += action;
        }

        [Conditional(LogMgr.DEBUG), Conditional("UNITY_EDITOR"), Conditional("UNITY_STANDALONE")]
        public void UnregDrawGUI(System.Action action)
        {
            m_OnDrawGui -= action;
        }

        [Conditional(LogMgr.DEBUG), Conditional("UNITY_EDITOR"), Conditional("UNITY_STANDALONE")]
        private void OnGUI()
        {
            if (m_OnDrawGui != null) {
                if (m_ShowWindow) {
                    m_WndRc = GUI.Window(872, m_WndRc, DrawWindow, "Application Debug Window");
                }

                var rc = new Rect(m_WndRc.xMin, m_WndRc.yMin - 20, m_WndRc.width, 20);
                m_ShowWindow = GUI.Toggle(rc, m_ShowWindow, "");
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F2)) {
                m_ShowWindow = !m_ShowWindow;
            }

            var customKey = GetKeyDown();
            if (customKey != KeyCode.None && onKey != null) {
                onKey.Invoke(customKey);
            }
      
        }

        private void LateUpdate()
        {
//            if (Input.deviceOrientation != m_CurScreenOrientation
//#if UNITY_EDITOR
//            || Screen.height != m_fScreenHeight || Screen.width != m_fScreenWidth
//#endif
//            )
//            {
//                m_CurScreenOrientation = Input.deviceOrientation;
//                UIFitSafeScreen.SetSafeArea(Screen.safeArea);
//                if (onScreenOrientationChanged != null)
//                {
//                    onScreenOrientationChanged.Invoke(m_CurScreenOrientation);
//                }
//            }
        }

        public void SendKey(KeyCode key)
        {
            if (onKey != null) onKey.Invoke(key);
        }
        
        private static int CalcSiblingIndex(UIWindow lc, Transform canvasTransform, int depth)
        {
            var siblingIndex = 0;
            // 计算Sibling
            var count = canvasTransform.childCount;
            for (int i = count - 1; i >= 0; --i) {
                var top = canvasTransform.GetChild(i).GetComponent<UIWindow>();

                if (top == null || !top.enabled || top.Equals(lc))
                    continue;

                if (depth == 0) {
                    if (top.depth == 0 || top.depth <= MAX_DEPTH) {
                        siblingIndex = i + 1;
                        break;
                    }
                } else if (depth > MAX_DEPTH) {
                    if (depth >= top.depth) {
                        siblingIndex = i + 1;
                        break;
                    }
                } else {
                    if (top.depth != 0 && depth >= top.depth) {
                        siblingIndex = i + 1;
                        break;
                    }
                }
            }

            return siblingIndex;
        }

        public static void UpdateSiblingIndex(UIWindow lc, int depth)
        {
            var siblingIndex = CalcSiblingIndex(lc, lc.transform.parent, depth);
            lc.depth = depth;
            lc.transform.SetSiblingIndex(siblingIndex);
        }


        public static void ClearAll(List<Transform> ignoreList = null)
        {
            // Destroy all UI Elements
            var list = ListPool<Component>.Get();
            for (int i = 0; i < Instance.m_Canvases.Length; ++i) {
                var canvasTrans = Instance.m_Canvases[i].transform;
                for (int j = 0; j < canvasTrans.childCount; ++j) {
                    var t = canvasTrans.GetChild(j);
                    var wnd = t.GetComponent<UIWindow>();
                    if (!(wnd && wnd.depth > MAX_DEPTH)
                        &&(ignoreList == null || !ignoreList.Contains(t))
                        && ObjectPoolManager.IsPooled(t.gameObject)) {
                        list.Add(t);
                    }
                }
            }

            for (int i = 0; i < list.Count; ++i) {
                var go = list[i].gameObject;
                ObjectPoolManager.DestroyPooled(go);
            }

            ListPool<Component>.Release(list);
        }
    }
}