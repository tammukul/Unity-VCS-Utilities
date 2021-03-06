﻿/*
    The MIT License

    Copyright (c) 2018 Ian Diaz, https://shadowndacorner.com

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in
    all copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
    THE SOFTWARE.
*/

using System.IO;
using System.Threading;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.Diagnostics;

using System.Text;
using Debug = UnityEngine.Debug;
using DateTime = System.DateTime;
using TimeSpan = System.TimeSpan;

[VCSImplementation(DisplayName = "Git")]
public class GitVCS : AbstractVCSHelper
{
    [System.Serializable]
    struct LockFileStorage
    {
        public int PID;
        public List<LockedFile> Locks;
    }

    // This is mostly to clean up the class
    class GitOnGui
    {
        GUIContent ConfigDropdown = new GUIContent("Configuration");
        GUIContent LockedFileDropdown = new GUIContent("Locked Files");
        public bool configShown;
        bool lockedFiles;

        bool hasCheckedVersion;
        bool versionWorks;

        void OpenGitForWindowsDLPage()
        {
            //https://github.com/git-for-windows/git/releases
            var startInfo = new ProcessStartInfo();
            startInfo.FileName = "start";
            startInfo.Arguments = "https://github.com/git-for-windows/git/releases";
            startInfo.CreateNoWindow = true;
            startInfo.UseShellExecute = true;
            Process.Start(startInfo);
        }

        public void Draw(GitVCS vcs)
        {
            if ((configShown = EditorGUILayout.Foldout(configShown, ConfigDropdown)))
            {
                ++EditorGUI.indentLevel;
                // Username
                {
                    string value = EditorPrefs.HasKey(GitHelper.EditorPrefsKeys.UsernamePrefKey) ? EditorPrefs.GetString(GitHelper.EditorPrefsKeys.UsernamePrefKey) : "";
                    string newvalue = EditorGUILayout.TextField("Github Username", value);
                    if (newvalue != value)
                    {
                        EditorPrefs.SetString(GitHelper.EditorPrefsKeys.UsernamePrefKey, newvalue);
                    }
                }

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Automatically Lock Scenes on Save");
                GitHelper.SceneAutoLock = EditorGUILayout.Toggle(GitHelper.SceneAutoLock, GUILayout.ExpandWidth(true));
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Remote Lock Prevents Edits");
                GitHelper.PreventEditsOnRemoteLock = EditorGUILayout.Toggle(GitHelper.PreventEditsOnRemoteLock, GUILayout.ExpandWidth(true));
                EditorGUILayout.EndHorizontal();

                if (!hasCheckedVersion)
                {
                    if (GUILayout.Button("Check Git Compatibility"))
                    {
                        hasCheckedVersion = true;
                        GitHelper.RunGitCommand("version",
                            (proc) =>
                            {
                                if (!proc.WaitForExit(2000))
                                {
                                    return true;
                                }
                                return false;
                            },
                            result =>
                            {
                                // We know what version is required on Windows, must check
                                // later for OSX/Linux.
                                if (result.Contains("windows"))
                                {
                                    result = result.Replace("git version ", "");

                                    var split = result.Trim().Split('.');
                                    var majorVersion = int.Parse(split[0].Trim());
                                    var minorVersion = int.Parse(split[1].Trim());
                                    if (majorVersion > 2 || (majorVersion == 2 && minorVersion >= 16))
                                    {
                                        versionWorks = true;
                                    }
                                    else
                                    {
                                        versionWorks = false;
                                        if (EditorUtility.DisplayDialog("Version Control", "Git for Windows out of date, this plugin requires at least version 2.16.  Would you like to open the GitHub Releases page in your web browser?", "Yes", "No"))
                                        {
                                            OpenGitForWindowsDLPage();
                                        }
                                    }
                                }
                                else
                                {
                                    Debug.Log("Version checking only supported on Windows.  If the plugin doesn't work, ");
                                    Debug.Log("Git Version: " + result);
                                }
                            },
                            error =>
                            {
                                Debug.LogError(error);
                                return true;
                            }
                        );
                    }
                }
                else
                {
                    if (Application.platform != RuntimePlatform.WindowsEditor)
                    {
                        EditorGUILayout.LabelField("Check Console, version checking only functional on Windows");
                    }
                    else if (versionWorks)
                    {
                        EditorGUILayout.LabelField("Local git compatibility good!");
                    }
                    else
                    {
                        if (GUILayout.Button("Update Git for Windows (opens GitHub Releases page)"))
                        {
                            OpenGitForWindowsDLPage();
                        }
                    }
                }
                --EditorGUI.indentLevel;
            }

            if ((lockedFiles = EditorGUILayout.Foldout(lockedFiles, LockedFileDropdown)))
            {
                ++EditorGUI.indentLevel;
                foreach (var v in vcs.LockedFiles)
                {
                    GUILayout.Label(v.Key + ": Locked by " + v.Value.User);
                    EditorGUILayout.BeginHorizontal();

                    if (GUILayout.Button("Select"))
                    {
                        Selection.activeObject = AssetDatabase.LoadAssetAtPath<Object>(v.Value.Path);
                    }

                    if (vcs.IsFileLockedByLocalUser(v.Key))
                    {
                        if (GUILayout.Button("Unlock"))
                        {
                            vcs.GitUnlockFile(new string[] { v.Key });
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.Space();
                }
                --EditorGUI.indentLevel;
            }

            if (GUILayout.Button("Refresh Locks"))
            {
                vcs.RefreshGitLockTypes();
                vcs.RefreshGitLocks();
            }

            if (GUILayout.Button("Reset Configuration"))
            {
                GitHelper.EditorPrefsKeys.ResetConfig();
            }
        }
    }

    [System.Serializable]
    public class LockedFile
    {
        public string Path;
        public string User;
        public FileStream FileLock;
    }

    public HashSet<string> LockableExtensions = new HashSet<string> { };
    public HashSet<string> ModifiedPaths = new HashSet<string>();

    const string LockedFileKey = "git-vcs-lockedfiles-key";
    Dictionary<string, LockedFile> _lockedFiles;
    public Dictionary<string, LockedFile> LockedFiles
    {
        get
        {
            if (_lockedFiles == null)
            {
                LoadLockedFilesFromEditorPrefs();
            }
            return _lockedFiles;
        }
    }

    void UpdateLockedFiles()
    {
        var storage = new LockFileStorage();
        storage.PID = Process.GetCurrentProcess().Id;
        var list = new List<LockedFile>();
        foreach(var v in LockedFiles)
        {
            list.Add(v.Value);
        }
        storage.Locks = list;
        EditorPrefs.SetString(LockedFileKey, JsonUtility.ToJson(storage));
    }

    void LoadLockedFilesFromEditorPrefs(bool forceRebuild=false)
    {
        _lockedFiles = new Dictionary<string, LockedFile>();
        if (EditorPrefs.HasKey(LockedFileKey))
        {
            var storage = JsonUtility.FromJson<LockFileStorage>(EditorPrefs.GetString(LockedFileKey));
            
            // If this is a different run, let's refresh git locks
            if ((storage.PID != Process.GetCurrentProcess().Id) || forceRebuild)
            {
                RefreshGitLocks();
            }
            else
            {
                // Otherwise, this is just an assembly load and we can probably use the old locks
                foreach (var v in storage.Locks)
                {
                    _lockedFiles.Add(v.Path, v);
                }
            }
        }
    }

    public override bool IsActive
    {
        get
        {
            return FindGitRoot();
        }
    }

    public static string GitRoot
    {
        get;
        internal set;
    }
    
    static bool FindGitRoot()
    {
        if (GitRoot != null)
            return true;

        bool found = false;
        GitHelper.RunGitCommand("rev-parse --show-toplevel",
            proc =>
            {
                if (!proc.WaitForExit(5000))
                {
                    return true;
                }
                return false;
            },

            result =>
            {
                GitRoot = result;
                found = true;
            },

            error =>
            {
                if (error.Contains("fatal"))
                {
                    Debug.LogError("[Git VCS Init] " + error);
                    return true;
                }
                return false;
            }
        );
        return found;
    }

    bool inPlayMode = false;
    public override void Initialize()
    {
        if (!FindGitRoot())
        {
            Debug.LogError("[VCS] Unable to find .git folder, git support disabled");
            return;
        }

        Thread m_asyncthread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (inPlayMode)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    // Update locks
                    var newLockFiles = new Dictionary<string, LockedFile>();
                    GitHelper.RunGitCommand("lfs locks",
                        proc =>
                        {
                            if (!proc.WaitForExit(5000))
                            {
                                return true;
                            }
                            return false;
                        },

                        result =>
                        {
                            try
                            {
                                var parts = result.Split('\t');
                                var path = parts[0];
                                var user = parts[1];

                                var locked = new LockedFile();
                                locked.Path = path;
                                locked.User = user;
                                newLockFiles.Add(path.Trim(), locked);
                            }
                            catch (System.Exception ex)
                            {
                                Debug.LogError("[VCS Async] " + ex);
                            }
                        },

                        error =>
                        {
                            Debug.LogError("[VCS]" + error);
                            return true;
                        }
                    );

                    var changes = GenerateRecursiveModifiedList();

                    lock (_actionQueueLock)
                    {
                        var cnt = LockedFiles.Count;
                        int hash = 0;
                        foreach (var v in LockedFiles)
                            hash += v.Key.GetHashCode() ^ v.Value.User.GetHashCode();


                        int modPathHash = 0;
                        foreach (var v in ModifiedPaths)
                            modPathHash += v.GetHashCode();

                        _toRunOnMainThread.Enqueue(() =>
                        {
                            // Locked files
                            if (cnt == LockedFiles.Count)
                            {
                                int newhash = 0;
                                foreach (var v in LockedFiles)
                                    newhash += v.Key.GetHashCode() ^ v.Value.User.GetHashCode();

                                // Let's make sure that the super lazy hashes match
                                if (newhash == hash)
                                {
                                    foreach (var v in LockedFiles)
                                    {
                                        if (v.Value.FileLock != null)
                                            v.Value.FileLock.Close();
                                    }
                                    LockedFiles.Clear();
                                    _lockedFiles = newLockFiles;
                                    foreach (var v in LockedFiles)
                                    {
                                        if (v.Value.User != GitHelper.Username && GitHelper.PreventEditsOnRemoteLock)
                                        {
                                            try
                                            {
                                                v.Value.FileLock = new FileStream(v.Key, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                                            }
                                            catch (System.Exception ex)
                                            {
                                                Debug.LogError("Failed to create file lock for " + v.Key + ": " + ex);
                                            }
                                        }
                                    }
                                }
                            }

                            // Ensure that paths diffs haven't been modified externally
                            int nmodPathHash = 0;
                            foreach (var v in ModifiedPaths)
                                nmodPathHash += v.GetHashCode();

                            if (nmodPathHash == modPathHash)
                            {
                                ModifiedPaths.Clear();
                                foreach (var v in changes)
                                {
                                    ModifiedPaths.Add(v);
                                }
                            }

                            UpdateLockedFiles();
                        });
                    }
                    Thread.Sleep(2000);
                }
                catch(ThreadAbortException ex)
                {
                    throw ex;
                }
                catch(System.Exception ex)
                {
                    Debug.LogError("[VCS Async] " + ex);
                }
            }
        });

        m_asyncthread.Name = "Git Async Thread";
        m_asyncthread.Start();
        
        // Preserves locked files for play mode, etc
        AssemblyReloadEvents.beforeAssemblyReload += () =>
        {
            m_asyncthread.Abort();
            UpdateLockedFiles();
        };

        UnityEditor.SceneManagement.EditorSceneManager.sceneSaved += (scn) =>
        {
            if (GitHelper.SceneAutoLock)
            {
                if (string.IsNullOrEmpty(scn.path))
                    return;

                if (!IsFileLockedByLocalUser(scn.path))
                {
                    GitLockFile(new string[] { scn.path });
                }
            }
        };

        EditorApplication.playModeStateChanged += (s) =>
        {
            if (s == PlayModeStateChange.ExitingEditMode)
            {
                UpdateLockedFiles();
            }
        };

        EditorApplication.projectWindowItemOnGUI +=
            (string guid, Rect rect) =>
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var iconRect = rect;
                var oldBack = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0, 0, 0, 0);

                if (ModifiedPaths.Contains(path))
                {
                    GUI.Box(iconRect, VCSHelper.ModifiedItemIcon);
                }

                if (IsFileLocked(path))
                {
                    GUI.Box(iconRect, new GUIContent(VCSHelper.LocalLockIcon, "Locked by " + LockedFiles[path].User + " (local user)"));
                }
                else if (IsFileLocked(path))
                {
                    GUI.Box(iconRect, new GUIContent(VCSHelper.RemoteLockIcon, "Locked by " + LockedFiles[path].User));
                }


                GUI.backgroundColor = oldBack;
            };

        if (!GitHelper.Configured)
        {
            if (EditorUtility.DisplayDialog("Version Control", "You have not yet set up your GitHub username and you will not be able to lock files.  Would you like to open the configuration window?", "Yes", "No"))
            {
                VCSConfigWindow.OpenWindow();
            }
        }
        else
        {
            LoadLockedFilesFromEditorPrefs();
            RefreshGitLockTypes();
        }
    }

    // This way we can refresh locks asynchronously
    object _actionQueueLock = new object();
    Queue<System.Action> _toRunOnMainThread = new Queue<System.Action>();

    public override void Update()
    {
        inPlayMode = EditorApplication.isPlayingOrWillChangePlaymode;
        if (_toRunOnMainThread.Count > 0)
        {
            lock (_actionQueueLock)
            {
                // This should be minimal overhead, so we'll only run one per frame
                if (_toRunOnMainThread.Count > 0)
                {
                    try
                    {
                        _toRunOnMainThread.Dequeue()();
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError("[VCS Sync] " + ex);
                    }
                }
            }
        }
    }

    public string GetCurrentBranch()
    {
        string branch = null;
        GitHelper.RunGitCommand("branch",
            (proc) =>
            {
                if (!proc.WaitForExit(5000))
                    return true;
                return false;
            },
            result =>
            {
                var split = result.Trim().Split(' ');
                if (split.Length > 0 && split[0].Trim() == "*")
                {
                    branch = split[split.Length - 1].Trim();
                }
            },
            error=>
            {
                Debug.LogError("[VCS Branch] " + error);
                return true;
            }
        );
        return branch;
    }

    public override List<string> GetChangedFiles()
    {
        List<string> changed = new List<string>();

        // TODO: Handle when git directory is not the project root
        GitHelper.RunGitCommand("diff --name-only",
            proc =>
            {
                if (!proc.WaitForExit(5000))
                    return true;
                return false;
            },
            result =>
            {
                if (File.Exists(result))
                {
                    changed.Add(result.Replace('\\', '/'));
                }
            },
            error =>
            {
                if (error.Contains("warning") || error.Contains("line endings"))
                    return false;

                Debug.LogError("[VCS Diff] " + error);
                return true;
            }
        );

        return changed;
    }

    public override List<string> GetTrackedFiles()
    {
        List<string> tracked = new List<string>();

        // TODO: Handle when git directory is not the project root
        GitHelper.RunGitCommand("ls-tree -r " + GetCurrentBranch() + " --name-only",
            proc =>
            {
                if (!proc.WaitForExit(5000))
                    return true;
                return false;
            },
            result =>
            {
                if (File.Exists(result))
                {
                    tracked.Add(result.Replace('\\', '/'));
                }
            },
            error =>
            {
                if (error.Contains("warning"))
                    return false;

                Debug.LogError("[VCS Tracked] " + error);
                return true;
            }
        );

        return tracked;
    }

    public override void DiscardChanges()
    {
        var guids = Selection.assetGUIDs;
        var paths = new List<string>(guids.Length);
        var changed = new HashSet<string>(GenerateRecursiveModifiedList());
        foreach (var v in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(v).Replace('\\', '/');
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    var filepath = file.Replace('\\', '/');
                    if (changed.Contains(filepath))
                    {
                        paths.Add(filepath);
                    }
                }
            }
            else
            {
                if (changed.Contains(path))
                    paths.Add(path);
            }
        }

        if (paths.Count == 0)
        {
            EditorUtility.DisplayDialog("Version Control", "No changes to discard", "Okay");
            return;
        }

        var msg = new StringBuilder();
        msg.AppendLine("Are you sure you want to discard changes?  The following files will be reverted:\n");
        foreach (var v in paths)
            msg.AppendLine(v);

        if (EditorUtility.DisplayDialog("Version Control", msg.ToString(), "Yes", "No"))
        {
            var arguments = new StringBuilder();
            foreach (var v in paths)
            {
                arguments.Append('"' + v + "\" ");
            }
            GitRevertFile(paths.ToArray());
            AssetDatabase.Refresh();
        }
    }

    public override bool LockFile()
    {
        var path = ActiveTargetPath;
        if (File.Exists(path))
        {
            return GitLockFile(new string[]{path});
        }
        return false;
    }

    public override bool UnlockFile()
    {
        var path = ActiveTargetPath;
        if (File.Exists(path))
        {
            return GitUnlockFile(new string[] { path });
        }
        return false;
    }

    public override bool IsFileLockable(string path)
    {
        var ext = Path.GetExtension(path).ToLower();
        if (string.IsNullOrEmpty(ext))
            return false;

        return (LockableExtensions.Contains(Path.GetExtension(ext)));
    }

    public override bool IsFileLocked(string path)
    {
        return LockedFiles.ContainsKey(path);
    }

    public override bool IsFileLockedByLocalUser(string path)
    {
        return IsFileLocked(path) && LockedFiles[path].User == GitHelper.Username;
    }

    public override void RefreshLockedFiles()
    {
        RefreshGitLockTypes();
        LoadLockedFilesFromEditorPrefs(true);
    }

    GitOnGui _inst;
    public override void ConfigMenuOnGui()
    {
        // This is just to clean up the namespace
        if (_inst == null)
            _inst = new GitOnGui();

        _inst.Draw(this);
    }

    public override bool ContextMenuButtonEnabled(ContextMenuButton button)
    {
        switch(button)
        {
            case ContextMenuButton.Lock:
                return TargetPaths.Length == 1 && IsFileLockable(ActiveTargetPath) && !IsFileLocked(ActiveTargetPath);
            case ContextMenuButton.Unlock:
                return TargetPaths.Length == 1 && IsFileLockedByLocalUser(ActiveTargetPath);
            case ContextMenuButton.DiscardChanges:
                return true;
        }
        return false;
    }

    public override void HandleModifiedAsset()
    {
        UpdateModifiedFilesAsync();
    }

    public bool GitLockFile(string[] paths)
    {
        var cmdstring = new StringBuilder();
        foreach (var path in paths)
        {
            cmdstring.Append('"' + path + '"');
        }

        /*
        GitHelper.RunGitCommand("track -- " + cmdstring,
            proc =>
            {
                if (!proc.WaitForExit(5000))
                {
                    return true;
                }
                return false;
            }
        );*/

        bool hasError = GitHelper.RunGitCommand("lfs lock -- " + cmdstring,
            proc =>
            {
                try
                {
                    while (!proc.HasExited)
                    {
                        if (paths.Length > 1)
                        {
                            if (EditorUtility.DisplayCancelableProgressBar("Version Control", "Locking files " + (cmdstring.ToString()) + "...", 0))
                            {
                                proc.Kill();
                                return true;
                            }
                        }
                        else
                        {
                            if (EditorUtility.DisplayCancelableProgressBar("Version Control", "Locking file " + Path.GetFileName(paths[0]) + "...", 0))
                            {
                                proc.Kill();
                                return true;
                            }
                        }
                        Thread.Sleep(16);
                    }
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
                return false;
            },
            result =>
            {
                Debug.Log("[LFS Lock] " + result);
                if (!result.Contains("Locked"))
                {
                    // Failed for some reason and didn't go to std::error, search for updated locks.
                    RefreshGitLocks();
                }
                else
                {
                    foreach (var path in paths)
                    {
                        var locked = new LockedFile();
                        locked.Path = path;
                        locked.User = GitHelper.Username;
                        LockedFiles.Add(path.Trim(), locked);
                    }
                }
            },
            error =>
            {
                Debug.Log("Failed to lock " + cmdstring);
                Debug.LogError(error);
                return true;
            });

        EditorApplication.RepaintProjectWindow();
        UpdateLockedFiles();
        return !hasError;
    }

    public bool GitUnlockFile(string[] paths)
    {
        var cmdstring = new StringBuilder();
        foreach (var path in paths)
        {
            cmdstring.Append('"' + path + '"');
        }

        bool hasError = GitHelper.RunGitCommand("lfs unlock -- " + cmdstring.ToString(),
            proc =>
            {
                try
                {
                    while (!proc.HasExited)
                    {
                        if (paths.Length > 1)
                        {
                            if (EditorUtility.DisplayCancelableProgressBar("Version Control", "Unlocking files " + (cmdstring.ToString()) + "...", 0))
                            {
                                proc.Kill();
                                return true;
                            }
                        }
                        else
                        {
                            if (EditorUtility.DisplayCancelableProgressBar("Version Control", "Unlocking file " + Path.GetFileName(paths[0]) + "...", 0))
                            {
                                proc.Kill();
                                return true;
                            }
                        }
                        Thread.Sleep(16);
                    }
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
                return false;
            },
            result =>
            {
                Debug.Log("[LFS Unlock] " + result);
                if (!result.Contains("Unlocked"))
                {
                    // Failed for some reason and didn't go to std::error, search for updated locks.
                    RefreshGitLocks();
                }
                else
                {
                    foreach (var path in paths)
                    {
                        LockedFiles.Remove(path.Trim());
                    }
                }
            },
            error =>
            {
                EditorUtility.DisplayDialog("Version Control", "Error while unlocking file: " + error, "Okay");

                // If it's erroring because it's already locked with local changes, ignore.
                // Otherwise, someone else locked the file before we did so everything is confused
                if (!error.Contains("uncommitted"))
                {
                    RefreshGitLocks();
                }

                return true;
            }
        );

        EditorApplication.RepaintProjectWindow();
        UpdateLockedFiles();
        return !hasError;
    }

    public void GitRevertFile(string[] paths)
    {
        // Yes this generates a lot of garbage but it's fine
        var rPath = new List<string>();
        var tracked = new HashSet<string>(GetChangedFiles());

        foreach(var v in paths)
        {
            if (tracked.Contains(v))
            {
                rPath.Add(v);
            }
            else
            {
                if (File.Exists(v))
                {
                    File.Delete(v);
                }
            }
        }

        if (rPath.Count == 0)
        {
            //Debug.Log("[VCS Discard] No git reverts necessary");
            return;
        }
        var cmdstring = new StringBuilder();
        foreach (var path in rPath)
        {
            cmdstring.Append('"' + path + '"' );
        }

        GitHelper.RunGitCommand("checkout -- " + cmdstring.ToString(),
            (proc) =>
            {
                if (!proc.WaitForExit(2000))
                {
                    return true;
                }
                return false;
            },
            result =>
            {
                Debug.Log("[VCS Discard] " + result);
            },
            error =>
            {
                Debug.LogError(error);
                return true;
            });
        UpdateLockedFiles();
    }

    public void RefreshGitLocks()
    {
        foreach (var v in LockedFiles)
        {
            if (v.Value.FileLock != null)
            {
                v.Value.FileLock.Close();
            }
        }

        LockedFiles.Clear();

        GitHelper.RunGitCommand("lfs locks",
            proc =>
            {
                try
                {
                    while (!proc.HasExited)
                    {
                        if (EditorUtility.DisplayCancelableProgressBar("Version Control", "Refreshing LFS locks...", 0))
                        {
                            proc.Kill();
                            return true;
                        }
                        Thread.Sleep(16);
                    }
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
                return false;
            },

            result =>
            {
                var parts = result.Split('\t');
                var path = parts[0];
                var user = parts[1];

                var locked = new LockedFile();
                locked.Path = path;
                locked.User = user;
                if (user != GitHelper.Username && GitHelper.PreventEditsOnRemoteLock)
                {
                    try
                    {
                        locked.FileLock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError("Failed to create file lock for " + path + ": " + ex);
                    }
                }
                LockedFiles.Add(path.Trim(), locked);
            },

            error =>
            {
                Debug.LogError(error);
                return true;
            }
        );

        UpdateLockedFiles();
        EditorApplication.RepaintProjectWindow();
    }

    public void RefreshGitLockTypes()
    {
        LockableExtensions.Clear();
        GitHelper.RunGitCommand("lfs track",
            proc =>
            {
                if (!proc.WaitForExit(2000))
                {
                    Debug.LogError("VC: LFS type check timed out");
                    return true;
                }
                return false;
            },
            result =>
            {
                var tracked = result.Trim();
                if (tracked.StartsWith("Listing"))
                    return;

                var split = tracked.Split(' ');
                var ext = split[0].Trim();
                LockableExtensions.Add(ext.ToLower().Replace("*.", "."));
            },
            err =>
            {
                Debug.LogError(err);
                return true;
            }
        );
        UpdateLockedFiles();
    }

    public List<string> GenerateRecursiveModifiedList()
    {
        var changes = GetChangedFiles();

        // Getting untracked files
        GitHelper.RunGitCommand("ls-files --others --exclude-standard",
            proc =>
            {
                if (!proc.WaitForExit(500))
                {
                    Debug.Log("[VCS] Timed out waiting for modified file list");
                    return true;
                }
                return false;
            },
            result =>
            {
                if (File.Exists(result))
                {
                    changes.Add(result);
                }
            }
        );

        foreach (var v in changes.ToArray())
        {
            var temp = Path.GetDirectoryName(v);

            // Basically assets
            // Not the best way to do this but fuck it
            while (temp.Length > 5)
            {
                changes.Add(temp.Replace('\\', '/'));
                temp = Path.GetDirectoryName(temp);
            }
        }
        return changes;
    }

    public void UpdateModifiedFiles()
    {
        lock (_actionQueueLock)
        {
            ModifiedPaths.Clear();
            foreach (var v in GenerateRecursiveModifiedList())
            {
                ModifiedPaths.Add(v);
            }
        }
    }

    public void UpdateModifiedFilesAsync()
    {
        try
        {
            var list = GenerateRecursiveModifiedList();
            int modPathHash = 0;
            foreach (var v in ModifiedPaths)
                modPathHash += v.GetHashCode();

            lock (_actionQueueLock)
            {
                _toRunOnMainThread.Enqueue(() =>
                {
                    int nModPathHash = 0;
                    foreach (var v in ModifiedPaths)
                        nModPathHash += v.GetHashCode();

                    if (nModPathHash == modPathHash)
                    {
                        ModifiedPaths.Clear();
                        foreach(var v in list)
                        {
                            ModifiedPaths.Add(v);
                        }
                    }
                });
            }
        }
        catch(System.Exception ex)
        {
            // fail silently, okay here
        }
    }
}
