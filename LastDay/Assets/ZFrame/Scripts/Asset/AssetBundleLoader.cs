using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TinyJSON;

namespace ZFrame.Asset
{
    public sealed class AssetBundleLoader : AssetLoader
    {
        public static AssetBundleLoader I { get { return Instance as AssetBundleLoader; } }

        static string m_persistentDataPath;
        public static string persistentDataPath {
            get {
                if (m_persistentDataPath == null) {
#if UNITY_EDITOR || UNITY_STANDALONE
                    m_persistentDataPath = SystemTools.GetDirPath(Application.dataPath) + "/Issets/PersistentData";
#else
					m_persistentDataPath = Application.persistentDataPath;
#endif
                }
                return m_persistentDataPath;
            }
        }
        static string m_streamingAssetPath;
        public static string streamingAssetsPath {
            get {
                if (m_streamingAssetPath == null) {
#if UNITY_EDITOR
                    m_streamingAssetPath = SystemTools.GetDirPath(Application.dataPath) + "/Issets/StreamingAssets";
#else
					m_streamingAssetPath = Application.streamingAssetsPath;
#endif
                }
                return m_streamingAssetPath;
            }
        }

#if UNITY_EDITOR
        public const string DIR_ASSETS = "RefAssets";
#endif

        public const string MD5 = "md5";
        public const string FILE_LIST = "filelist";

        public static string CombinePath(string path1, string path2)
        {
            return string.Concat(path1, '/', path2);
        }

        public const string ASSETBUNDLE_FOLDER = "AssetBundles";
        static string m_bundleRoot;
        public static string bundleRootPath {
            get {
                if (m_bundleRoot == null) {
                    m_bundleRoot = CombinePath(persistentDataPath, ASSETBUNDLE_FOLDER);
                }
                return m_bundleRoot;
            }
        }

        public const string DOWNLOAD_FOLDER = "Downloads";
        static string m_downloadRoot;
        public static string downloadRootPath {
            get {
                if (m_downloadRoot == null) {
                    m_downloadRoot = CombinePath(persistentDataPath, DOWNLOAD_FOLDER);
                }
                return m_downloadRoot;
            }
        }

        static string m_streamingRoot;
        public static string streamingRootPath {
            get {
                if (m_streamingRoot == null) {
                    m_streamingRoot = CombinePath(streamingAssetsPath, ASSETBUNDLE_FOLDER);
                }
                return m_streamingRoot;
            }
        }

        private AssetBundleManifest m_Manifest;
        public readonly List<string> allAssetBundles = new List<string>();
        protected override void Awaking()
        {
            base.Awaking();

            var ab = AssetBundle.LoadFromFile(GetAssetBundlePath("AssetBundles", false));
            m_Manifest = ab.LoadAsset("AssetBundleManifest", typeof(AssetBundleManifest)) as AssetBundleManifest;
        }

        private void OnDestroy()
        {
            AssetBundle.UnloadAllAssetBundles(true);
        }

        private Pool<AssetBundleRef> m_ABPool = new Pool<AssetBundleRef>(null, null);
        protected override void ReleaseAbr(AbstractAssetBundleRef ab)
        {
            m_ABPool.Release(ab as AssetBundleRef);
        }

        private string GetStreamingPath(string fileName)
        {
            return CombinePath(streamingRootPath, fileName);
        }

        private string GetFilePath(string fileName)
        {
            var path = GetStreamingPath(fileName);
            if (!path.OrdinalStartsWith("file://")) path = "file://" + path;
            return path;
        }

        private string GetAssetBundlePath(string assetbundleName, bool forcedStreaming)
        {
            if (!forcedStreaming) {
                var suitPath = CombinePath(bundleRootPath, assetbundleName);
                if (File.Exists(suitPath)) return suitPath;

                suitPath = CombinePath(downloadRootPath, assetbundleName);
                if (File.Exists(suitPath)) return suitPath;
            }

            return CombinePath(streamingRootPath, assetbundleName);
        }

        public override bool ScheduleTask(AsyncLoadingTask task)
        {
#if false   
            // 在目前的资源结构规划下，自动判断依赖性价比不高，先不用。
            var ds = m_Manifest.GetAllDependencies(task.bundleName);
            foreach (var abName in ds) {
                var dTask = m_TaskPool.Get();
                dTask.SetBundleAsset(abName, null);
                base.ScheduleTask(dTask);
            }
#endif
            return base.ScheduleTask(task);
        }

        private void InitBundleList(string joStr)
        {
            var joAssets = JSON.Load(joStr)["Assets"] as ProxyObject;
            if (joAssets != null) {
                foreach (var kv in joAssets) {
                    if (kv.Value["siz"] > 0) allAssetBundles.Add(kv.Key);
                }
            }
        }
        
        protected override bool IsBundleExist(AsyncLoadingTask task)
        {
            if (task.bundleType == BundleType.FileAsset) return true;
            
            return string.IsNullOrEmpty(task.bundleName) || allAssetBundles.Contains(task.bundleName);
        }

        protected override IEnumerator PerformTask(AsyncLoadingTask task)
        {
            string abName = task.bundleName;
            string suitPath = GetAssetBundlePath(task.bundleName, task.forcedStreaming);

            var req = AssetBundle.LoadFromFileAsync(suitPath);
            while (!req.isDone) {
                yield return null;
                OnLoading(abName, req.progress * 0.5f);
            }

            AssetBundle ab = req.assetBundle;
            if (ab != null) {
                Object[] allAssets = empty;
                if (!ab.isStreamedSceneAssetBundle) {
                    if (task.allowCache) {
                        // 这是一个普通的资源包，获取里面的所有Assets以缓存。
                        AssetBundleRequest abReq = ab.LoadAllAssetsAsync();
                        while (!abReq.isDone) {
                            yield return null;
                            OnLoading(abName, Mathf.Min(0.999f, 0.5f + abReq.progress * 0.5f));
                        }
                        allAssets = abReq.allAssets;
                    }
                } else {
                    // 这是一个场景资源包，不需要获取里面的Assets
                    // 需要保留文件镜像的内存直到场景被载入。
                }
                OnLoading(abName, 1f);

                var bundle = m_ABPool.Get();
                bundle.Init(abName, ab, allAssets, task.method);
                task.bundle = bundle;
            } else {
                LogMgr.W("{0}加载失败。", task);
            }
        }

        protected override IEnumerator PerformMultiTasks(IList<AsyncLoadingTask> tasks, IAssetProgress prog)
        {
            var time = Time.realtimeSinceStartup;
            var strbld = new System.Text.StringBuilder();

            int maxTask = tasks.Count;
            int totalTask = maxTask + 1;
            AsyncOperation last = null;
            for (int i = 0, n = 0; i < tasks.Count; ++n) { 
            //for (int i = maxTask - 1; i >= 0; --i) {
                var task = tasks[i];
                if (!TryGetAssetBundle(task.bundleName, out task.bundle)) {
                    strbld.AppendLine(task.ToString());
                    string suitPath = GetAssetBundlePath(task.bundleName, false);
                    var req = AssetBundle.LoadFromFileAsync(suitPath);
                    // 读取文件一个一个来
                    while (!req.isDone) {
                        yield return null;
                        if (prog != null) {
                            var progress = n + (last == null ? req.progress : (req.progress + last.progress) / 2);
                            prog.SetProgress(progress / totalTask);
                        }
                    }

                    var ab = req.assetBundle;
                    if (ab) {
                        var bundle = m_ABPool.Get();
                        bundle.Init(task.bundleName, ab, empty, task.method);
                        task.bundle = bundle;

                        if (task.allowCache && !ab.isStreamedSceneAssetBundle) {
                            // 将继续实例化内部资源
                            task.async = ab.LoadAllAssetsAsync();
                            last = task.async;
                            ++i; continue;
                        }
                    }
                    last = null;
                    FinishLoadindBundle(task);
                }
                task.OnBundleLoaded();

                if (prog != null) {
                    prog.SetProgress((n + 1f) / totalTask);
                    prog.OnBundleLoaded(task.bundleName, task.bundle);
                }

                tasks.RemoveAt(i);
                AsyncLoadingTask.Release(task);
            }

            // 等待资源加载完成
            var max = 0f;
            while (tasks.Count > 0) {
                yield return null;
                for (int i = 0; i < tasks.Count;) {
                    //for (int i = tasks.Count - 1; i >= 0; --i) {
                    var task = tasks[i];
                    if (task.async.isDone) {
                        var allAssets = (task.async as AssetBundleRequest).allAssets;
                        (task.bundle as AssetBundleRef).CacheAssets(allAssets);
                        FinishLoadindBundle(task);
                        if (!LoadAssetFromCache(task)) {
                            yield return LoadAssetFromBundle(task);
                        }
                        task.OnBundleLoaded();
                        if (prog != null) {
                            prog.OnBundleLoaded(task.bundleName, task.bundle);
                        }

                        tasks.RemoveAt(i);
                        AsyncLoadingTask.Release(task);
                    } else {
                        if (max < task.async.progress) {
                            max = task.async.progress;
                        }
                        ++i;
                    }
                }

                if (prog != null) {
                    prog.SetProgress(Mathf.Min(0.999f, (maxTask + max) / totalTask));
                }
            }
            if (prog != null) {
                prog.SetProgress(1f);
            }

            LogMgr.Log(string.Format("加载资源耗时{0}秒：\n{1}", Time.realtimeSinceStartup - time, strbld.ToString()));
        }

        /// <summary>
        /// 开始加载一个AssetBundle, 只会从streamingRootPath目录下加载
        /// </summary>
        /// <param name="assetPath">AssetBundle的路径</param>
        /// <param name="method">是否运行完全卸载</param>
        /// <param name="onLoaded">加载完成回调处理</param>
        /// <param name="needMD5">是否需要计算文件的MD5值</param>
        public void StreamingTask(string assetPath, LoadMethod method, DelegateAssetBundleLoaded onLoaded = null)
        {
            var task = NewTask(assetPath, method, onLoaded);
            task.forcedStreaming = true;
            ScheduleTask(task);
        }

        public override AsyncOperation LoadLevelAsync(string path, LoadSceneMode mode = LoadSceneMode.Single)
        {
            // 解析出资源包和资源对象
            string assetbundleName, assetName;
            GetAssetpath(path, out assetbundleName, out assetName);

            if (assetbundleName == string.Empty) {
                assetName = path;
            }

            return SceneManager.LoadSceneAsync(assetName, mode);
        }

        public string md5 { get; private set; }
        public IEnumerator LoadMD5()
        {
            yield return LoadingAsset(typeof(TextAsset), GetFilePath(MD5));
            md5 = loadedObj as string;
        }

        public IEnumerator LoadFileList()
        {
            string filePath = CombinePath(bundleRootPath, FILE_LIST);
            if (!File.Exists(filePath)) {
                yield return LoadingAsset(typeof(TextAsset), GetFilePath(FILE_LIST));
                LogMgr.D("Saving '{0}' to '{1}'", FILE_LIST, filePath);
                var asset = loadedObj as string;
                InitBundleList(asset);
                SystemTools.NeedDirectory(bundleRootPath);
                File.WriteAllText(filePath, asset);
            } else {
                InitBundleList(File.ReadAllText(filePath));
            }
        }

        private void OnApplicationQuit()
        {
            AssetsTransfer.StopTransfer();
        }
        public IEnumerator TransferAll()
        {
            yield return null;
#if false
	        if (!AssetsTransfer.IsLock && !AssetsTransfer.IsTransfering && File.Exists(bundleListPath)) {
	            string[] lines = File.ReadAllLines(bundleListPath);
	            List<System.Object> liTransfer = new List<System.Object>();
	            for (int i = 1; i < lines.Length; ++i) {
	                string assetName = lines[i];
	                string bundlePath = Path.Combine(bundleRootPath, assetName);
	                if (!File.Exists(bundlePath)) {
	                    liTransfer.Add(assetName);
	                }
	            }

	            if (liTransfer.Count > 0) {
	                Log(string.Format("Start Transfer {0}", liTransfer.Count));
	                AssetsTransfer.Total = liTransfer.Count;
	                AssetsTransfer.Count = 0;
	                foreach (string asset in liTransfer) {
	                    string url = Path.Combine(streamingRootPath, asset);
	                    if (!url.Contains("://")) {
	                        url = "file://" + url;
	                    }
	                    WWW www = new WWW(url);
	                    yield return www;
	        
	                    if (www.error == null) {
	                        new AssetsTransfer(www.bytes, Path.Combine(bundleRootPath, asset));
	                    }
	                    AssetsTransfer.Count++;
	                }
	            }
	        }
#endif
        }

    }
}