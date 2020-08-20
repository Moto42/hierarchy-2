using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Experimental.SceneManagement;
using UnityEditorInternal;
using UnityEditor.Callbacks;
using UnityEditor.UIElements;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Hierarchy2
{
    [InitializeOnLoad]
    public sealed class HierarchyEditor
    {
        internal const int GLOBAL_SPACE_OFFSET_LEFT = 16 * 2;

        static HierarchyEditor instance;

        public static HierarchyEditor Instance
        {
            get
            {
                if (instance == null)
                    instance = new HierarchyEditor();
                return instance;
            }
            private set { instance = value; }
        }

        Dictionary<int, HierarchyCanvas> hierarchyCanvas = new Dictionary<int, HierarchyCanvas>();
        Dictionary<int, UnityEngine.Object> selectedComponents = new Dictionary<int, UnityEngine.Object>();
        Dictionary<string, string> dicComponents = new Dictionary<string, string>(StringComparer.Ordinal);
        UnityEngine.Object activeComponent;

        GUIContent tooltipContent = new GUIContent();
        const string componentDefaultTooltip = "\n\nM2-click: Edit\nM3-click: Instant Inspector";

        const string customIconDefaultTooltip = "M2-click: To customize";

        HierarchySettings settings;

        HierarchySettings.ThemeData ThemeData
        {
            get { return settings.usedTheme; }
        }

        int deepestRow = int.MinValue;
        int previousRowIndex = int.MinValue;

        int sceneIndex = 0;
        Scene currentScene;
        Scene previousScene;

        public static bool IsMultiScene
        {
            get { return SceneManager.sceneCount > 1; }
        }

        bool selectionStyleAfterInvoke = false;
        bool checkingAllHierarchy = false;

        Event currentEvent;

        RowItem rowItem = new RowItem();
        RowItem previousElement = null;
        WidthUse widthUse = WidthUse.zero;

        static HierarchyEditor()
        {
            if (instance == null)
                instance = new HierarchyEditor();
        }

        public HierarchyEditor()
        {
            InternalReflection();
            EditorApplication.update += EditorAwake;
            AssetDatabase.importPackageCompleted += ImportPackageCompleted;
        }

        static Dictionary<string, Type> dicInternalEditorType = new Dictionary<string, Type>();

        static Type SceneHierarchyWindow;
        static Type SceneHierarchy;
        static Type GameObjectTreeViewGUI;

        static FieldInfo m_SceneHierarchy;
        static FieldInfo m_TreeView;
        static PropertyInfo gui;
        static FieldInfo k_IconWidth;

        static Func<SearchableEditorWindow> lastInteractedHierarchyWindowDelegate;
        static Func<IEnumerable> GetAllSceneHierarchyWindowsDelegate;
        static Func<GameObject, Rect, bool, bool> IconSelectorShowAtPositionDelegate;
        static Action<Rect, UnityEngine.Object, int> DisplayObjectContextMenuDelegate;

        public static Action OnRepaintHierarchyWindowCallback;
        public static Action OnWindowsReorderedCallback;

        static void InternalReflection()
        {
            dicInternalEditorType = typeof(Editor).Assembly.GetTypes().ToDictionary(type => type.FullName);

            FieldInfo refreshHierarchy = typeof(EditorApplication).GetField(nameof(refreshHierarchy),
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo OnRepaintHierarchyWindow = typeof(HierarchyEditor).GetMethod(nameof(OnRepaintHierarchyWindow),
                BindingFlags.NonPublic | BindingFlags.Static);
            Delegate refreshHierarchyDelegate =
                Delegate.CreateDelegate(typeof(EditorApplication.CallbackFunction), OnRepaintHierarchyWindow);
            refreshHierarchy.SetValue(null, refreshHierarchyDelegate);


            FieldInfo windowsReordered = typeof(EditorApplication).GetField(nameof(windowsReordered),
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo OnWindowsReordered = typeof(HierarchyEditor).GetMethod(nameof(OnWindowsReordered),
                BindingFlags.NonPublic | BindingFlags.Static);
            Delegate windowsReorderedDelegate =
                Delegate.CreateDelegate(typeof(EditorApplication.CallbackFunction), OnWindowsReordered);
            windowsReordered.SetValue(null, windowsReorderedDelegate);

            {
                dicInternalEditorType.TryGetValue(nameof(UnityEditor) + "." + nameof(SceneHierarchyWindow),
                    out SceneHierarchyWindow);
                dicInternalEditorType.TryGetValue(nameof(UnityEditor) + "." + nameof(GameObjectTreeViewGUI),
                    out GameObjectTreeViewGUI); //GameObjectTreeViewGUI : TreeViewGUI
                dicInternalEditorType.TryGetValue(nameof(UnityEditor) + "." + nameof(SceneHierarchy),
                    out SceneHierarchy);
            }

            FieldInfo s_LastInteractedHierarchy = SceneHierarchyWindow.GetField(nameof(s_LastInteractedHierarchy),
                BindingFlags.NonPublic | BindingFlags.Static);

            MethodInfo lastInteractedHierarchyWindow = SceneHierarchyWindow
                .GetProperty(nameof(lastInteractedHierarchyWindow), BindingFlags.Static | BindingFlags.Public)
                .GetGetMethod();
            lastInteractedHierarchyWindowDelegate =
                Delegate.CreateDelegate(typeof(Func<SearchableEditorWindow>), lastInteractedHierarchyWindow) as
                    Func<SearchableEditorWindow>;

            MethodInfo GetAllSceneHierarchyWindows = SceneHierarchyWindow.GetMethod(nameof(GetAllSceneHierarchyWindows),
                BindingFlags.Static | BindingFlags.Public);
            GetAllSceneHierarchyWindowsDelegate =
                Delegate.CreateDelegate(typeof(Func<IEnumerable>), GetAllSceneHierarchyWindows) as Func<IEnumerable>;


            {
                m_SceneHierarchy = SceneHierarchyWindow.GetField(nameof(m_SceneHierarchy),
                    BindingFlags.NonPublic | BindingFlags.Instance);
                m_TreeView =
                    SceneHierarchy.GetField(nameof(m_TreeView), BindingFlags.NonPublic | BindingFlags.Instance);
                gui = m_TreeView.FieldType.GetProperty(nameof(gui).ToLower(),
                    BindingFlags.Public | BindingFlags.Instance);
                k_IconWidth =
                    GameObjectTreeViewGUI.GetField(nameof(k_IconWidth), BindingFlags.Public | BindingFlags.Instance);
            }

            MethodInfo DisplayObjectContextMenu = typeof(EditorUtility)
                .GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single
                (
                    method => method.Name == nameof(DisplayObjectContextMenu) &&
                              method.GetParameters()[1].ParameterType == typeof(UnityEngine.Object)
                );
            DisplayObjectContextMenuDelegate =
                Delegate.CreateDelegate(typeof(Action<Rect, UnityEngine.Object, int>), DisplayObjectContextMenu) as
                    Action<Rect, UnityEngine.Object, int>;


            Type IconSelector = typeof(EditorWindow).Assembly.GetTypes().Single(type =>
                type.BaseType == typeof(EditorWindow) && type.Name == nameof(IconSelector)) as Type;
            MethodInfo ShowAtPosition = IconSelector.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single
            (
                method => method.Name == nameof(ShowAtPosition) &&
                          method.GetParameters()[0].ParameterType == typeof(UnityEngine.Object)
            );
            IconSelectorShowAtPositionDelegate =
                Delegate.CreateDelegate(typeof(Func<GameObject, Rect, bool, bool>), ShowAtPosition) as
                    Func<GameObject, Rect, bool, bool>;
        }

        public static IEnumerable GetAllSceneHierarchyWindows() => GetAllSceneHierarchyWindowsDelegate();

        public static void DisplayObjectContextMenu(Rect rect, UnityEngine.Object unityObject, int value) =>
            DisplayObjectContextMenuDelegate(rect, unityObject, value);

        public static bool IconSelectorShowAtPosition(GameObject gameObject, Rect rect, bool value) =>
            IconSelectorShowAtPositionDelegate(gameObject, rect, value);

        static void OnRepaintHierarchyWindow()
        {
            OnRepaintHierarchyWindowCallback?.Invoke();
        }

        static void OnWindowsReordered()
        {
            OnWindowsReorderedCallback?.Invoke();
        }

        void SetObjectIconWidth(float value)
        {
            foreach (EditorWindow window in GetAllSceneHierarchyWindowsDelegate())
            {
                object gameObjectTreeViewGUIObject =
                    gui.GetValue(m_TreeView.GetValue(m_SceneHierarchy.GetValue(window)));
                k_IconWidth.SetValue(gameObjectTreeViewGUIObject, value);
            }
        }

        GUIStyle selectionStyle;
        Texture2D selectionImageOverride;

        void OverrideSelectionColorStyle(Color color)
        {
            if (selectionStyle == null)
            {
                Type Styles = null;
                dicInternalEditorType.TryGetValue(typeof(TreeView).FullName + nameof(GUI) + "+" + nameof(Styles),
                    out Styles);
                FieldInfo selectionStyleField = Styles.GetField(nameof(selectionStyle));
                selectionStyle = selectionStyleField.GetValue(null) as GUIStyle;
            }

            selectionImageOverride = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            selectionImageOverride.SetPixel(0, 0, color);
            selectionImageOverride.Apply();
            selectionStyle.normal.background = selectionImageOverride;
        }

        void EditorAwake()
        {
            settings = HierarchySettings.GetAssets();
            if (settings is null)
                return;

            OnSettingsChanged(nameof(settings.components));
            OnSettingsChanged(nameof(settings.displayObjectIcon));

            settings.onSettingsChanged += OnSettingsChanged;
            EditorApplication.hierarchyWindowItemOnGUI += HierarchyOnGUI;

            if (settings.activeHierarchy)
                Invoke();
            else
                Dispose();

            EditorApplication.update -= EditorAwake;
        }

        void ImportPackageCompleted(string packageName)
        {
        }

        void OnSettingsChanged(string param)
        {
            switch (param)
            {
                case nameof(ThemeData.selectionColor):
                    // OverrideSelectionColorStyle(ThemeData.selectionColor);
                    break;

                case nameof(settings.displayObjectIcon):
                    SetObjectIconWidth(settings.displayObjectIcon ? 16 : 0);
                    break;

                case nameof(settings.components):
                    dicComponents.Clear();
                    foreach (string componentType in settings.components)
                    {
                        if (!dicComponents.ContainsKey(componentType))
                            dicComponents.Add(componentType, componentType);
                    }

                    break;
            }

            EditorApplication.RepaintHierarchyWindow();
        }

        public void Invoke()
        {
            EditorSceneManager.newSceneCreated += OnNewSceneCreated;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            EditorSceneManager.sceneLoaded += OnSceneLoaded;
            EditorSceneManager.sceneUnloaded += OnSceneUnloaded;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorSceneManager.sceneDirtied += OnSceneDirtied;

            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            EditorApplication.modifierKeysChanged += OnModifierKeysChanged;

            PrefabUtility.prefabInstanceUpdated += OnPrefabUpdated;
            PrefabStage.prefabStageOpened += OnPrefabStageOpened;
            PrefabStage.prefabStageClosing += OnPrefabStageClosing;

            EditorApplication.update += OnEditorUpdate;

            selectionStyleAfterInvoke = false;
            EditorApplication.RepaintHierarchyWindow();
            EditorApplication.RepaintProjectWindow();
        }

        public void Dispose()
        {
            EditorSceneManager.newSceneCreated -= OnNewSceneCreated;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneClosed -= OnSceneClosed;
            EditorSceneManager.sceneLoaded -= OnSceneLoaded;
            EditorSceneManager.sceneUnloaded -= OnSceneUnloaded;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorSceneManager.sceneDirtied -= OnSceneDirtied;

            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            EditorApplication.modifierKeysChanged -= OnModifierKeysChanged;

            PrefabUtility.prefabInstanceUpdated -= OnPrefabUpdated;
            PrefabStage.prefabStageOpened -= OnPrefabStageOpened;
            PrefabStage.prefabStageClosing -= OnPrefabStageClosing;

            EditorApplication.update -= OnEditorUpdate;

            ClearAllCanvas();

            foreach (EditorWindow window in GetAllSceneHierarchyWindowsDelegate())
            {
                window.titleContent.text = "Hierarchy";
            }

            EditorApplication.RepaintHierarchyWindow();
            EditorApplication.RepaintProjectWindow();
        }

        double lastTimeSinceStartup = EditorApplication.timeSinceStartup;

        void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup - lastTimeSinceStartup >= 1)
            {
                DelayCall();
                lastTimeSinceStartup = EditorApplication.timeSinceStartup;
            }
        }

        void DelayCall()
        {
            if (checkingAllHierarchy == true)
            {
                foreach (EditorWindow window in GetAllSceneHierarchyWindowsDelegate())
                {
                    if (window.rootVisualElement.childCount == 0)
                    {
                        CreateCanvas(window);
                        window.titleContent.text = "Hierarchy 2";
                    }
                }

                checkingAllHierarchy = false;
            }

            if (hierarchyChangedRequireUpdating == true)
            {
                // OverrideSelectionColorStyle(ThemeData.selectionColor);
                hierarchyChangedRequireUpdating = false;
            }
        }

        void OnModifierKeysChanged()
        {
        }

        [DidReloadScripts]
        static void OnEditorCompiled()
        {
        }

        void OnNewSceneCreated(Scene scene, NewSceneSetup setup, NewSceneMode mode)
        {
        }

        void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
        }

        void OnSceneClosed(Scene scene)
        {
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
        }

        void OnSceneUnloaded(Scene scene)
        {
        }

        void OnSceneSaved(Scene scene)
        {
        }

        void OnSceneDirtied(Scene scene)
        {
        }

        bool hierarchyChangedRequireUpdating = false;

        void OnHierarchyChanged()
        {
            if (selectionImageOverride == null)
            {
                hierarchyChangedRequireUpdating = true;
            }
        }

        void OnPrefabUpdated(GameObject prefab)
        {
        }

        bool prefabStageChanged = false;

        void OnPrefabStageOpened(PrefabStage stage)
        {
            if (!settings.displayObjectIcon)
                prefabStageChanged = true;
        }

        void OnPrefabStageClosing(PrefabStage stage)
        {
            if (!settings.displayObjectIcon)
                prefabStageChanged = true;
        }

        internal HierarchyCanvas GetHierarchyCanvas()
        {
            if (lastInteractedHierarchyWindowDelegate() != null)
            {
                if (lastInteractedHierarchyWindowDelegate().rootVisualElement.childCount == 0)
                    CreateCanvas(lastInteractedHierarchyWindowDelegate());

                HierarchyCanvas canvas = lastInteractedHierarchyWindowDelegate().rootVisualElement
                    .FindChildren<HierarchyCanvas>(nameof(HierarchyCanvas));
                canvas = lastInteractedHierarchyWindowDelegate().rootVisualElement
                    .FindChildren<HierarchyCanvas>(nameof(HierarchyCanvas));
                return canvas;
            }

            return null;
        }

        void CreateCanvas(EditorWindow hierarchyWindow)
        {
            int hash = hierarchyWindow.GetHashCode();
            if (hierarchyCanvas.ContainsKey(hash))
                return;

            var root = hierarchyWindow.rootVisualElement;
            HierarchyCanvas canvas = new HierarchyCanvas();
            canvas.name = nameof(HierarchyCanvas);
            root.Add(canvas);
            hierarchyCanvas.Add(hash, canvas);
        }

        void ClearAllCanvas()
        {
            foreach (var canvas in hierarchyCanvas)
                canvas.Value.RemoveFromHierarchy();
            hierarchyCanvas.Clear();
        }

        public HierarchyLocalData GetHierarchyLocalData(Scene scene)
        {
            HierarchyLocalData hierarchyLocalData = null;
            if (HierarchyLocalData.GetInstance(scene, out hierarchyLocalData))
                return hierarchyLocalData;
            return hierarchyLocalData = CreateHierarchyLocalData(scene);
        }

        HierarchyLocalData CreateHierarchyLocalData(Scene scene)
        {
            GameObject hierarchyLocalDataObject = new GameObject("HierarchyLocalData", typeof(HierarchyLocalData));
            EditorSceneManager.MoveGameObjectToScene(hierarchyLocalDataObject, scene);
            DirtyScene(scene);
            return hierarchyLocalDataObject.GetComponent<HierarchyLocalData>();
        }

        void HierarchyOnGUI(int selectionID, Rect selectionRect)
        {
            currentEvent = Event.current;

            if (currentEvent.type == EventType.KeyDown && currentEvent.keyCode == KeyCode.H && currentEvent.control)
            {
                if (!settings.activeHierarchy)
                    Invoke();
                else
                    Dispose();

                settings.activeHierarchy = !settings.activeHierarchy;
                currentEvent.Use();
            }

            if (!settings.activeHierarchy)
                return;

            if (currentEvent.control && currentEvent.keyCode == KeyCode.D)
                return;

            if (currentEvent.type == EventType.Layout)
            {
                if (prefabStageChanged)
                {
                    SetObjectIconWidth(settings.displayObjectIcon ? 16 : 0);
                    prefabStageChanged = false;
                }

                return;
            }

            checkingAllHierarchy = true;

            if (selectionStyleAfterInvoke == false && currentEvent.type == EventType.MouseDown)
            {
                // OverrideSelectionColorStyle(ThemeData.selectionColor);
                selectionStyleAfterInvoke = true;
            }

            rowItem.Dispose();
            rowItem.ID = selectionID;
            rowItem.gameObject = EditorUtility.InstanceIDToObject(rowItem.ID) as GameObject;
            rowItem.rect = selectionRect;
            rowItem.rowIndex = GetRowIndex(selectionRect);
            rowItem.isSelected = InSelection(selectionID);
            rowItem.isFirstRow = IsFirstRow(selectionRect);
            rowItem.isFirstElement = IsFirstElement(selectionRect);

            rowItem.isNull = rowItem.gameObject == null ? true : false;

            if (!rowItem.isNull)
            {
                rowItem.isHeader = rowItem.name.Contains(settings.headerPrefix);
                rowItem.isDirty = EditorUtility.IsDirty(selectionID);

                if (true && !rowItem.isHeader && rowItem.isDirty)
                {
                    rowItem.isPrefab = PrefabUtility.IsPartOfAnyPrefab(rowItem.gameObject);

                    if (rowItem.isPrefab)
                        rowItem.isPrefabMissing = PrefabUtility.IsPrefabAssetMissing(rowItem.gameObject);
                }
            }

            rowItem.isRootObject = rowItem.isNull || rowItem.gameObject.transform.parent == null ? true : false;
            rowItem.isMouseHovering = selectionRect.Contains(currentEvent.mousePosition);

            if (rowItem.isFirstRow) //Instance always null
            {
                sceneIndex = 0;

                if (deepestRow > previousRowIndex)
                    deepestRow = previousRowIndex;

                // if (settings.displayVersion)
                //     BottomRightArea(selectionRect);

                // Background(selectionRect);
            }

            if (rowItem.isNull)
            {
                if (!IsMultiScene)
                    currentScene = SceneManager.GetActiveScene();
                else
                {
                    if (!rowItem.isFirstRow && sceneIndex < SceneManager.sceneCount - 1)
                        sceneIndex++;
                    currentScene = SceneManager.GetSceneAt(sceneIndex);
                }

                RenameSceneInHierarchy();

                if (settings.displayRowBackground)
                {
                    if (deepestRow != rowItem.rowIndex)
                        DisplayRowBackground();
                }

                previousElement = rowItem;
                previousRowIndex = rowItem.rowIndex;
                previousScene = currentScene;

                if (previousRowIndex > deepestRow)
                    deepestRow = previousRowIndex;
                return;
            }
            else
            {
                if (rowItem.isFirstElement)
                {
                    if (deepestRow > previousRowIndex)
                        deepestRow = previousRowIndex;
                    deepestRow -= rowItem.rowIndex;

                    if (IsMultiScene)
                    {
                        if (!previousElement.isNull)
                        {
                            for (int i = 0; i < SceneManager.sceneCount; ++i)
                            {
                                if (SceneManager.GetSceneAt(i) == rowItem.gameObject.scene)
                                {
                                    sceneIndex = i;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (IsMultiScene)
                {
                }

                rowItem.nameRect = rowItem.rect;
                GUIStyle nameStyle = TreeStyleFromFont(FontStyle.Normal);
                rowItem.nameRect.width = nameStyle.CalcSize(new GUIContent(rowItem.gameObject.name)).x;

                if (settings.displayObjectIcon)
                    rowItem.nameRect.x += 16;

                var isPrefabMode = PrefabStageUtility.GetCurrentPrefabStage() != null ? true : false;

                if (settings.displayRowBackground && deepestRow != rowItem.rowIndex)
                {
                    if (isPrefabMode)
                    {
                        if (rowItem.gameObject.transform.parent == null) //Should use row index instead.
                        {
                            if (deepestRow != 0)
                                DisplayRowBackground();
                        }
                    }
                    else
                        DisplayRowBackground();
                }

                if (rowItem.isHeader && rowItem.isRootObject)
                {
                    ElementAsHeader();
                    goto FINISH;
                }


                HierarchyLocalData hld = null;
                bool hasHierarchyLocalData =
                    HierarchyLocalData.instances.TryGetValue(rowItem.gameObject.scene, out hld);
                if (hasHierarchyLocalData)
                {
                    rowItem.hasCustom = hld.TryGetCustomRowData(rowItem.gameObject, out rowItem.customRowItem);
                }

                if (rowItem.hasCustom)
                    CustomRow();

                if (settings.displayTreeView && !rowItem.isRootObject)
                    DisplayTreeView();

                if (settings.displayObjectIcon && settings.displayCustomObjectIcon)
                    DisplayCustomObjectIcon();

                widthUse = WidthUse.zero;
                widthUse.left += GLOBAL_SPACE_OFFSET_LEFT;
                if (isPrefabMode) widthUse.left -= 2;
                widthUse.afterName = rowItem.nameRect.x + rowItem.nameRect.width;

                if (settings.displayDirtyTrack && rowItem.isDirty)
                    DisplayDirtyTrack();

                widthUse.afterName += settings.offSetIconAfterName;

                DisplayEditableIcon();

                // DisplayNoteIcon();

                widthUse.afterName += 8;

                if (settings.displayTag && !rowItem.gameObject.CompareTag("Untagged"))
                {
                    if (!settings.onlyDisplayWhileMouseEnter ||
                        (settings.contentDisplay & HierarchySettings.ContentDisplay.Tag) !=
                        HierarchySettings.ContentDisplay.Tag ||
                        ((settings.contentDisplay & HierarchySettings.ContentDisplay.Tag) ==
                            HierarchySettings.ContentDisplay.Tag && rowItem.isMouseHovering))
                    {
                        DisplayTag();
                    }
                }

                if (settings.displayLayer && rowItem.gameObject.layer != 0)
                {
                    if (!settings.onlyDisplayWhileMouseEnter ||
                        (settings.contentDisplay & HierarchySettings.ContentDisplay.Layer) !=
                        HierarchySettings.ContentDisplay.Layer ||
                        ((settings.contentDisplay & HierarchySettings.ContentDisplay.Layer) ==
                            HierarchySettings.ContentDisplay.Layer && rowItem.isMouseHovering))
                    {
                        DisplayLayer();
                    }
                }

                if (settings.displayComponents)
                {
                    if (!settings.onlyDisplayWhileMouseEnter ||
                        (settings.contentDisplay & HierarchySettings.ContentDisplay.Component) !=
                        HierarchySettings.ContentDisplay.Component ||
                        ((settings.contentDisplay & HierarchySettings.ContentDisplay.Component) ==
                            HierarchySettings.ContentDisplay.Component && rowItem.isMouseHovering))
                    {
                        DisplayComponents();
                    }
                }

                ElementEvent(rowItem);

                FINISH:
                if (settings.displayGrid)
                    DisplayGrid();

                previousElement = rowItem;
                previousRowIndex = rowItem.rowIndex;
                previousScene = currentScene;

                if (previousRowIndex > deepestRow)
                {
                    deepestRow = previousRowIndex;
                }
            }
        }

        GUIStyle TreeStyleFromFont(FontStyle fontStyle)
        {
            GUIStyle style;
            switch (fontStyle)
            {
                case FontStyle.Bold:
                    style = new GUIStyle(Styles.TreeBoldLabel);
                    break;

                case FontStyle.Italic:
                    style = new GUIStyle(Styles.TreeLabel);
                    break;

                case FontStyle.BoldAndItalic:
                    style = new GUIStyle(Styles.TreeBoldLabel);
                    break;

                default:
                    style = new GUIStyle(Styles.TreeLabel);
                    break;
            }

            return style;
        }

        void CustomRow()
        {
            if (currentEvent.type != EventType.Repaint)
                return;

            if (rowItem.customRowItem.useBackground)
            {
                Color guiColor = GUI.color;
                GUI.color = rowItem.customRowItem.backgroundColor;
                Texture2D texture = rowItem.customRowItem.backgroundStyle == CustomRowItem.BackgroundStyle.Ramp
                    ? Resources.Ramp8x8White
                    : Resources.PixelWhite;
                Rect rect;

                if (rowItem.customRowItem.backgroundMode == CustomRowItem.BackgroundMode.Name)
                    rect = RectFromRight(rowItem.nameRect, rowItem.nameRect.width, 0);
                else
                {
                    rect = RectFromRight(rowItem.rect, rowItem.rect.width + 16, 0);
                    rect.x += 16;
                    rect.xMin = 32;
                }

                GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill);
                GUI.color = guiColor;
            }
        }

        void ElementAsHeader()
        {
            if (currentEvent.type != EventType.Repaint)
                return;

            if (!rowItem.gameObject.CompareTag(settings.headerDefaultTag))
                rowItem.gameObject.tag = settings.headerDefaultTag;

            var rect = EditorGUIUtility.PixelsToPoints(RectFromLeft(rowItem.rect, Screen.width, 0));
            rect.y = rowItem.rect.y;
            rect.height = rowItem.rect.height;
            rect.x += GLOBAL_SPACE_OFFSET_LEFT;
            rect.width -= GLOBAL_SPACE_OFFSET_LEFT;

            Color guiColor = GUI.color;
            GUI.color = ThemeData.colorHeaderBackground;
            GUI.DrawTexture(rect, Resources.PixelWhite, ScaleMode.StretchToFill);

            var content = new GUIContent(rowItem.name.Remove(0, settings.headerPrefix.Length));
            rect.x += (rect.width - Styles.Header.CalcSize(content).x) / 2;
            GUI.color = ThemeData.colorHeaderTitle;
            GUI.Label(rect, content, Styles.Header);
            GUI.color = guiColor;
        }

        void ElementEvent(RowItem element)
        {
            if (currentEvent.type == EventType.KeyDown)
            {
                if (currentEvent.control && currentEvent.shift && currentEvent.alt &&
                    currentEvent.keyCode == KeyCode.C && lastInteractedHierarchyWindowDelegate() != null)
                    CollapseAll();
            }

            if (currentEvent.type == EventType.KeyUp &&
                currentEvent.keyCode == KeyCode.F2 &&
                Selection.gameObjects.Length > 1)
            {
                var window = SelectionsRenamePopup.ShowPopup();
                currentEvent.Use();
                return;
            }

            if (element.rect.Contains(currentEvent.mousePosition) && currentEvent.type == EventType.MouseUp &&
                currentEvent.button == 2)
            {
                Undo.RegisterCompleteObjectUndo(element.gameObject,
                    element.gameObject.activeSelf ? "Inactive object" : "Active object");
                element.gameObject.SetActive(!element.gameObject.activeSelf);
                DirtyScene(element.gameObject.scene);
                currentEvent.Use();
                return;
            }
        }

        void StaticIcon(RowItem element)
        {
            if (!element.isStatic) return;

            var rect = element.rect;
            rect = RectFromRight(rect, 3, 0);

            if (currentEvent.type == EventType.MouseUp &&
                currentEvent.button == 1 &&
                rect.Contains(currentEvent.mousePosition))
            {
                GenericMenu staticMenu = new GenericMenu();
                staticMenu.AddItem(new GUIContent("Apply All Children"), settings.applyStaticTargetAndChild,
                    () => { settings.applyStaticTargetAndChild = !settings.applyStaticTargetAndChild; });
                staticMenu.AddSeparator("");
                staticMenu.AddItem(new GUIContent("True"), element.gameObject.isStatic ? true : false,
                    () => { element.gameObject.isStatic = !element.gameObject.isStatic; });
                staticMenu.AddItem(new GUIContent("False"), !element.gameObject.isStatic ? true : false,
                    () => { element.gameObject.isStatic = !element.gameObject.isStatic; });
                staticMenu.ShowAsContext();
                currentEvent.Use();
            }

            GUISeparator(rect, Color.magenta);
        }

        void ApplyStaticTargetAndChild(Transform target, bool value)
        {
            target.gameObject.isStatic = value;

            for (int i = 0; i < target.childCount; ++i)
                ApplyStaticTargetAndChild(target.GetChild(i), value);
        }

        void DisplayCustomObjectIcon()
        {
            var rect = RectFromRight(rowItem.nameRect, 16, rowItem.nameRect.width + 1);
            rect.height = 16;

            if (currentEvent.type == EventType.MouseUp && currentEvent.button == 1 &&
                rect.Contains(currentEvent.mousePosition))
            {
                IconSelectorShowAtPositionDelegate(rowItem.gameObject, rect, true);
                currentEvent.Use();
            }

            if (currentEvent.type == EventType.Repaint)
            {
                if (rect.Contains(currentEvent.mousePosition))
                {
                    GUI.Box(rect, new GUIContent("", customIconDefaultTooltip), GUIStyle.none);
                }

                Texture2D icon = AssetPreview.GetMiniThumbnail(rowItem.gameObject);

                if (icon.name == "GameObject Icon" || icon.name == "d_GameObject Icon" || icon.name == "Prefab Icon" ||
                    icon.name == "d_Prefab Icon" || icon.name == "PrefabModel Icon" ||
                    icon.name == "d_PrefabModel Icon")
                    return;

                Color guiColor = GUI.color;
                GUI.color = rowItem.rowIndex % 2 != 0 ? ThemeData.colorRowEven : ThemeData.colorRowOdd;
                GUI.DrawTexture(rect, Resources.PixelWhite);
                GUI.color = guiColor;
                GUI.DrawTexture(rect, icon, ScaleMode.ScaleToFit);
            }
        }


        void DisplayDirtyTrack()
        {
            var rect = RectFromLeft(rowItem.nameRect, 7, ref widthUse.afterName);

            if (currentEvent.type == EventType.Repaint)
            {
                GUIStyle style;

                if (rowItem.gameObject.activeSelf)
                {
                    if (rowItem.isPrefab)
                        style = rowItem.isPrefabMissing ? Styles.PR_BrokenPrefabLabel : Styles.PR_PrefabLabel;
                    else
                        style = Styles.lineStyle;
                }
                else
                {
                    if (rowItem.isPrefab)
                        style = rowItem.isPrefabMissing
                            ? Styles.PR_DisabledBrokenPrefabLabel
                            : Styles.PR_DisabledPrefabLabel;
                    else
                        style = Styles.PR_DisabledLabel;
                }

                style.Draw(rect, "*", false, false, rowItem.isSelected, true);
            }
        }

        void DisplayEditableIcon()
        {
            if (rowItem.gameObject.hideFlags == HideFlags.NotEditable)
            {
                Rect lockRect = RectFromLeft(rowItem.nameRect, 12, ref widthUse.afterName);

                if (currentEvent.type == EventType.Repaint)
                {
                    GUI.color = ThemeData.colorLockIcon;
                    GUI.DrawTexture(lockRect, Resources.lockIconOn, ScaleMode.ScaleToFit);
                    GUI.color = Color.white;
                }

                if (currentEvent.type == EventType.MouseUp &&
                    currentEvent.button == 1 &&
                    lockRect.Contains(currentEvent.mousePosition))
                {
                    GenericMenu lockMenu = new GenericMenu();

                    GameObject gameObject = rowItem.gameObject;

                    lockMenu.AddItem(new GUIContent("Unlock"), false, () =>
                    {
                        Undo.RegisterCompleteObjectUndo(gameObject, "Unlock...");
                        gameObject.hideFlags = HideFlags.None;
                        EditorUtility.SetDirty(gameObject);
                    });
                    lockMenu.ShowAsContext();
                    currentEvent.Use();
                }
            }
        }

        void DisplayNoteIcon()
        {
            // if (!element.hasLocalData || element.data.note == "")
            //     return;

            // var iconRect = RectFromLeft(element.nameRect, 14, ref widthUse.afterName);
            // if (currentEvent.type == EventType.Repaint)
            // {
            //     GUIContent content = new GUIContent("", element.data.note);
            //     GUI.Box(iconRect, content, GUIStyle.none);
            //     GUI.color = Color.yellow;
            //     GUI.DrawTexture(iconRect, Resources.NoteIcon, ScaleMode.ScaleToFit);
            //     GUI.color = Color.white;
            // }

            // if (currentEvent.type == EventType.MouseUp && currentEvent.button == 1 && iconRect.Contains(currentEvent.mousePosition))
            // {
            //     GenericMenu noteMenu = new GenericMenu();
            //     noteMenu.AddItem(new GUIContent("Remove Note"), false, () =>
            //     {
            //         element.data.note = "";
            //     });
            //     noteMenu.ShowAsContext();
            //     currentEvent.Use();
            // }

            // widthUse.afterName += 2;
        }

        void DisplayComponents()
        {
            var components = rowItem.gameObject.GetComponents(typeof(Component)).ToList<UnityEngine.Object>();
            var rendererComponent = rowItem.gameObject.GetComponent<Renderer>();
            bool hasMaterial = rendererComponent != null && rendererComponent.sharedMaterial != null;

            if (hasMaterial)
            {
                for (int i = 0; i < rendererComponent.sharedMaterials.Length; ++i)
                {
                    Material sharedMat = rendererComponent.sharedMaterials[i];
                    components.Add(sharedMat);
                }
            }

            int length = components.Count;
            bool separator = false;
            float widthUsedCached = 0;
            if (settings.componentAlignment == HierarchySettings.ElementAlignment.AfterName)
            {
                widthUsedCached = widthUse.afterName;
                widthUse.afterName += 4;
            }
            else
            {
                widthUsedCached = widthUse.right;
                widthUse.right += 2;
            }

            for (int i = 0; i < length; ++i)
            {
                var component = components[i];

                try
                {
                    Type comType = component.GetType();

                    if (comType != null)
                    {
                        bool isMono = false;
                        if (comType.BaseType == typeof(MonoBehaviour)) isMono = true;

                        switch (settings.componentDisplayMode)
                        {
                            case HierarchySettings.ComponentDisplayMode.ScriptOnly:
                                if (!isMono)
                                    continue;
                                break;

                            case HierarchySettings.ComponentDisplayMode.Below:
                                if (!dicComponents.ContainsKey(comType.Name))
                                    continue;
                                break;

                            case HierarchySettings.ComponentDisplayMode.Ignore:
                                if (dicComponents.ContainsKey(comType.Name))
                                    continue;
                                break;
                        }

                        Rect rect = Rect.zero;

                        if (settings.componentAlignment == HierarchySettings.ElementAlignment.AfterName)
                            rect = RectFromLeft(rowItem.nameRect, settings.componentSize, ref widthUse.afterName);
                        else
                            rect = RectFromRight(rowItem.rect, settings.componentSize, ref widthUse.right);


                        if (hasMaterial && i == length - rendererComponent.sharedMaterials.Length &&
                            settings.componentDisplayMode != HierarchySettings.ComponentDisplayMode.ScriptOnly)
                        {
                            for (int m = 0; m < rendererComponent.sharedMaterials.Length; ++m)
                            {
                                var sharedMaterial = rendererComponent.sharedMaterials[m];

                                if (sharedMaterial == null) continue;
                                ComponentIcon(sharedMaterial, comType, rect, true);

                                if (settings.componentAlignment == HierarchySettings.ElementAlignment.AfterName)
                                    rect = RectFromLeft(rowItem.nameRect, settings.componentSize,
                                        ref widthUse.afterName);
                                else
                                    rect = RectFromRight(rowItem.rect, settings.componentSize, ref widthUse.right);
                            }

                            separator = true;
                            break;
                        }

                        ComponentIcon(component, comType, rect);

                        if (settings.componentAlignment == HierarchySettings.ElementAlignment.AfterName)
                            widthUse.afterName += settings.componentSpacing;
                        else
                            widthUse.right += settings.componentSpacing;

                        separator = true;
                    }
                }
                catch (System.Exception)
                {
                    continue;
                }
            }

            if (separator && currentEvent.type == EventType.Repaint)
            {
                if (settings.componentAlignment == HierarchySettings.ElementAlignment.AfterName)
                    GUISeparator(RectFromLeft(rowItem.nameRect, 2, widthUsedCached), ThemeData.colorGrid);
                // else
                //     GUISeparator(RectFromRight(element.rect, 2, widthUsedCached), ThemeData.colorGrid);
            }
        }

        void ComponentIcon(UnityEngine.Object component, Type componentType, Rect rect, bool isMaterial = false)
        {
            int comHash = component.GetHashCode();

            if (currentEvent.type == EventType.Repaint)
            {
                Texture image = EditorGUIUtility.ObjectContent(component, componentType).image;

                if (selectedComponents.ContainsKey(comHash))
                {
                    Color guiColor = GUI.color;
                    GUI.color = ThemeData.comSelBGColor;
                    GUI.DrawTexture(rect, Resources.PixelWhite, ScaleMode.StretchToFill);
                    GUI.color = guiColor;
                }

                string tooltip = isMaterial ? component.name : componentType.Name;
                tooltipContent.tooltip = tooltip + componentDefaultTooltip;
                GUI.Box(rect, tooltipContent, GUIStyle.none);

                GUI.DrawTexture(rect, image, ScaleMode.ScaleToFit);
            }


            if (rect.Contains(currentEvent.mousePosition))
            {
                if (currentEvent.type == EventType.MouseDown)
                {
                    if (currentEvent.button == 0)
                    {
                        if (currentEvent.control)
                        {
                            if (!selectedComponents.ContainsKey(comHash))
                            {
                                selectedComponents.Add(comHash, component);
                                activeComponent = component;
                            }
                            else
                            {
                                selectedComponents.Remove(comHash);
                            }

                            currentEvent.Use();
                            return;
                        }

                        selectedComponents.Clear();
                        selectedComponents.Add(comHash, component);
                        activeComponent = component;
                        currentEvent.Use();
                        return;
                    }

                    if (currentEvent.button == 1)
                    {
                        if (currentEvent.control)
                        {
                            GenericMenu componentGenericMenu = new GenericMenu();

                            componentGenericMenu.AddItem(new GUIContent("Remove All Component"), false, () =>
                            {
                                if (!selectedComponents.ContainsKey(comHash))
                                    selectedComponents.Add(comHash, component);

                                foreach (var selectedComponent in selectedComponents.ToList())
                                {
                                    if (selectedComponent.Value is Material)
                                        continue;

                                    selectedComponents.Remove(selectedComponent.Key);
                                    Undo.DestroyObjectImmediate(selectedComponent.Value);
                                }

                                selectedComponents.Clear();
                            });
                            componentGenericMenu.ShowAsContext();
                        }
                        else
                        {
                            DisplayObjectContextMenuDelegate(rect, component, 0);
                        }

                        currentEvent.Use();
                        return;
                    }
                }

                if (currentEvent.type == EventType.MouseUp)
                {
                    if (currentEvent.button == 2)
                    {
                        List<UnityEngine.Object> inspectorComponents = new List<UnityEngine.Object>();

                        foreach (var selectedComponent in selectedComponents)
                            inspectorComponents.Add(selectedComponent.Value);

                        if (!selectedComponents.ContainsKey(comHash))
                            inspectorComponents.Add(component);

                        var window = InstantInspector.OpenEditor();
                        window.Fill(inspectorComponents,
                            currentEvent.alt ? InstantInspector.FillMode.Add : InstantInspector.FillMode.Default);
                        window.Focus();

                        currentEvent.Use();
                        return;
                    }
                }
            }

            if (selectedComponents.Count > 0 &&
                currentEvent.type == EventType.MouseDown &&
                currentEvent.button == 0 &&
                !currentEvent.control &&
                !rect.Contains(currentEvent.mousePosition))
            {
                selectedComponents.Clear();
                activeComponent = null;
            }
        }

        void BottomRightArea(Rect rect)
        {
            // var content = new GUIContent(string.Format("{0}", VERSION));
            // rect = RectFromRight(rect, EditorStyles.miniBoldLabel.CalcSize(content).x, 0);
            // rect.y += Screen.height - 59;
            // GUI.color = new Color(.5f, .5f, .5f, .2f);
            // GUI.Label(rect, content, EditorStyles.miniBoldLabel);
            // GUI.color = Color.white;
        }

        void Background(Rect rect)
        {
            // rect.y += 16;
            // rect.xMin = 0;
            // rect.height = Screen.height;
            // GUI.color = new Color(.4f, .4f, .4f, 1);
            // GUI.DrawTexture(rect, Assets.PixelWhite);
            // GUI.color = Color.white;
        }

        void DisplayTag()
        {
            GUIContent tagContent = new GUIContent(rowItem.gameObject.tag);

            var style = Styles.Tag;
            style.normal.textColor = ThemeData.tagColor;
            Rect rect;

            if (settings.tagAlignment == HierarchySettings.ElementAlignment.AfterName)
            {
                rect = RectFromLeft(rowItem.nameRect, style.CalcSize(tagContent).x, ref widthUse.afterName);

                if (currentEvent.type == EventType.Repaint)
                {
                    GUISeparator(RectFromLeft(rowItem.nameRect, 1, widthUse.afterName), ThemeData.colorGrid);
                    GUI.Label(rect, tagContent, style);
                }
            }
            else
            {
                rect = RectFromRight(rowItem.rect, style.CalcSize(tagContent).x, ref widthUse.right);

                if (currentEvent.type == EventType.Repaint)
                {
                    GUISeparator(RectFromRight(rowItem.rect, 1, widthUse.right), ThemeData.colorGrid);
                    GUI.Label(rect, tagContent, style);
                }
            }

            if (currentEvent.type == EventType.MouseUp && currentEvent.button == 1 &&
                rect.Contains(currentEvent.mousePosition))
            {
                GenericMenu menuTags = new GenericMenu();
                GameObject gameObject = rowItem.gameObject;

                menuTags.AddItem(new GUIContent("Apply All Children"), settings.applyTagTargetAndChild,
                    () => { settings.applyTagTargetAndChild = !settings.applyTagTargetAndChild; });
                menuTags.AddSeparator("");

                foreach (var tag in InternalEditorUtility.tags)
                {
                    menuTags.AddItem(new GUIContent(tag), gameObject.tag == tag ? true : false, () =>
                    {
                        if (settings.applyTagTargetAndChild)
                            ApplyTagTargetAndChild(gameObject.transform, tag);
                        else
                            gameObject.tag = tag;

                        DirtyScene(gameObject.scene);
                    });
                }

                menuTags.ShowAsContext();
                currentEvent.Use();
            }
        }

        void ApplyTagTargetAndChild(Transform target, string tag)
        {
            target.gameObject.tag = tag;

            for (int i = 0; i < target.childCount; ++i)
                ApplyTagTargetAndChild(target.GetChild(i), tag);
        }

        void DisplayLayer()
        {
            GUIContent layerContent = new GUIContent(LayerMask.LayerToName(rowItem.gameObject.layer));
            var style = Styles.Layer;
            style.normal.textColor = ThemeData.layerColor;
            Rect rect;

            if (settings.layerAlignment == HierarchySettings.ElementAlignment.AfterName)
            {
                rect = RectFromLeft(rowItem.nameRect, style.CalcSize(layerContent).x, ref widthUse.afterName);

                if (currentEvent.type == EventType.Repaint)
                {
                    GUISeparator(RectFromLeft(rowItem.nameRect, 1, widthUse.afterName), ThemeData.colorGrid);
                    GUI.Label(rect, layerContent, style);
                }
            }
            else
            {
                rect = RectFromRight(rowItem.rect, style.CalcSize(layerContent).x, ref widthUse.right);

                if (currentEvent.type == EventType.Repaint)
                {
                    GUISeparator(RectFromRight(rowItem.rect, 1, widthUse.right), ThemeData.colorGrid);
                    GUI.Label(rect, layerContent, style);
                }
            }

            if (currentEvent.type == EventType.MouseUp && currentEvent.button == 1 &&
                rect.Contains(currentEvent.mousePosition))
            {
                GenericMenu menuLayers = new GenericMenu();
                GameObject gameObject = rowItem.gameObject;

                menuLayers.AddItem(new GUIContent("Apply All Children"), settings.applyLayerTargetAndChild,
                    () => { settings.applyLayerTargetAndChild = !settings.applyLayerTargetAndChild; });
                menuLayers.AddSeparator("");

                foreach (string layer in InternalEditorUtility.layers)
                {
                    menuLayers.AddItem(new GUIContent(layer),
                        LayerMask.NameToLayer(layer) == gameObject.layer ? true : false, () =>
                        {
                            if (settings.applyLayerTargetAndChild)
                                ApplyLayerTargetAndChild(gameObject.transform, LayerMask.NameToLayer(layer));
                            else
                                gameObject.layer = LayerMask.NameToLayer(layer);

                            DirtyScene(gameObject.scene);
                        });
                }

                menuLayers.ShowAsContext();
                currentEvent.Use();
            }
        }

        void ApplyLayerTargetAndChild(Transform target, int layer)
        {
            target.gameObject.layer = layer;

            for (int i = 0; i < target.childCount; ++i)
                ApplyLayerTargetAndChild(target.GetChild(i), layer);
        }

        void DisplayRowBackground(bool nextRow = true)
        {
            if (currentEvent.type != EventType.Repaint)
                return;

            Rect rect = rowItem.rect;
            rect.xMin = -1;
            rect.width += 16;

            Color color = (rect.y / rect.height) % 2 == 0 ? ThemeData.colorRowEven : ThemeData.colorRowOdd;

            if (nextRow)
                rect.y += rect.height;

            Color guiColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Resources.PixelWhite, ScaleMode.StretchToFill);
            GUI.color = guiColor;
        }

        void DisplayGrid()
        {
            if (currentEvent.type != EventType.Repaint)
                return;

            var rect = rowItem.rect;

            rect.xMin = GLOBAL_SPACE_OFFSET_LEFT;
            rect.y += 15;
            rect.width += 16;
            rect.height = 1;

            Color guiColor = GUI.color;
            GUI.color = ThemeData.colorGrid;
            GUI.DrawTexture(rect, Resources.PixelWhite, ScaleMode.StretchToFill);
            GUI.color = guiColor;
        }

        void DisplayTreeView()
        {
            if (currentEvent.type != EventType.Repaint)
                return;

            Rect rect = rowItem.rect;

            rect.width = 40;
            rect.x -= 34;
            var t = rowItem.gameObject.transform.parent;

            Color guiColor = GUI.color;
            GUI.color = ThemeData.colorTreeView;

            if (t.childCount == 1 || t.GetChild(t.childCount - 1) == rowItem.gameObject.transform)
            {
                GUI.DrawTexture(rect, Resources.TreeTRIcon, ScaleMode.ScaleToFit);
            }
            else
            {
                GUI.DrawTexture(rect, Resources.TreeTRDIcon, ScaleMode.ScaleToFit);
            }

            while (t != null)
            {
                if (t.parent == null)
                    break;

                if (t == t.parent.GetChild(t.parent.childCount - 1))
                {
                    t = t.parent;
                    rect.x -= 14;
                    continue;
                }

                rect.x -= 14;
                GUI.DrawTexture(rect, Resources.TreeTDIcon, ScaleMode.ScaleToFit);
                t = t.parent;
            }

            GUI.color = guiColor;
        }

        GUIContent tmpSceneContent = new GUIContent();

        void RenameSceneInHierarchy()
        {
            string name = currentScene.name;
            if (name == "")
                return;

            var leftTitleWidthUsed = 48f;
#if UNITY_2019_1_OR_NEWER
            leftTitleWidthUsed += 24f;
#endif

            if (!currentScene.isLoaded)
                name = string.Format("{0} (not loaded", name);

            tmpSceneContent.text = name == "" ? "Untitled" : name;
            Vector2 size = Styles.TreeBoldLabel.CalcSize(tmpSceneContent);
            leftTitleWidthUsed += size.x;


            if (currentEvent.type == EventType.KeyDown &&
                currentEvent.keyCode == KeyCode.F2 &&
                rowItem.rect.Contains(currentEvent.mousePosition))
            {
                SceneRenamePopup.ShowPopup(currentScene);
            }
        }

        void CollapseAll()
        {
        }

        void DirtyScene(Scene scene)
        {
            if (EditorApplication.isPlaying)
                return;

            EditorSceneManager.MarkSceneDirty(scene);
        }

        bool IsFirstElement(Rect rect) => previousRowIndex > rect.y / rect.height;

        bool IsFirstRow(Rect rect) => rect.y / rect.height == 0;

        int GetRowIndex(Rect rect) => (int) (rect.y / rect.height);

        bool InSelection(int ID) => Selection.Contains(ID) ? true : false;

        bool IsElementDirty(int ID) => EditorUtility.IsDirty(ID);

        Rect RectFromRight(Rect rect, float width, float usedWidth)
        {
            usedWidth += width;
            rect.x = rect.x + rect.width - usedWidth;
            rect.width = width;
            return rect;
        }

        Rect RectFromRight(Rect rect, float width, ref float usedWidth)
        {
            usedWidth += width;
            rect.x = rect.x + rect.width - usedWidth;
            rect.width = width;
            return rect;
        }

        Rect RectFromRight(Rect rect, Vector2 offset, float width, ref float usedWidth)
        {
            usedWidth += width;
            rect.position += offset;
            rect.x = rect.x + rect.width - usedWidth;
            rect.width = width;
            return rect;
        }

        Rect RectFromLeft(Rect rect, float width, float usedWidth, bool usexmin = true)
        {
            if (usexmin)
                rect.xMin = 0;
            rect.x += usedWidth;
            rect.width = width;
            usedWidth += width;
            return rect;
        }

        Rect RectFromLeft(Rect rect, float width, ref float usedWidth, bool usexmin = true)
        {
            if (usexmin)
                rect.xMin = 0;
            rect.x += usedWidth;
            rect.width = width;
            usedWidth += width;
            return rect;
        }

        Rect RectFromLeft(Rect rect, Vector2 offset, float width, ref float usedWidth, bool usexmin = true)
        {
            if (usexmin)
                rect.xMin = 0;
            rect.position += offset;
            rect.x += usedWidth;
            rect.width = width;
            usedWidth += width;
            return rect;
        }

        void GUISeparator(Rect rect, Color color)
        {
            Color guiColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Resources.PixelWhite, ScaleMode.StretchToFill);
            GUI.color = guiColor;
        }

        struct WidthUse
        {
            public float left;
            public float right;
            public float afterName;

            public WidthUse(float left, float right, float afterName)
            {
                this.left = left;
                this.right = right;
                this.afterName = afterName;
            }

            public static WidthUse zero
            {
                get { return new WidthUse(0, 0, 0); }
            }
        }

        sealed class RowItem
        {
            public int ID = int.MinValue;
            public Rect rect;
            public Rect nameRect;
            public int rowIndex = 0;
            public GameObject gameObject;
            public bool isNull = true;
            public bool isPrefab = false;
            public bool isPrefabMissing = false;
            public bool isRootObject = false;
            public bool isSelected = false;
            public bool isFirstRow = false;
            public bool isFirstElement = false;
            public bool isHeader = false;
            public bool isDirty = false;
            public bool isMouseHovering = false;
            public bool hasCustom = false;
            public CustomRowItem customRowItem;

            public string name
            {
                get { return isNull ? "Null" : gameObject.name; }
            }

            public int childCount
            {
                get { return gameObject.transform.childCount; }
            }

            public Scene Scene
            {
                get { return gameObject.scene; }
            }

            public bool isStatic
            {
                get { return isNull ? false : gameObject.isStatic; }
            }

            public RowItem()
            {
            }

            public void Dispose()
            {
                ID = int.MinValue;
                gameObject = null;
                rect = Rect.zero;
                nameRect = Rect.zero;
                rowIndex = 0;
                isNull = true;
                isRootObject = false;
                isSelected = false;
                isFirstRow = false;
                isFirstElement = false;
                isHeader = false;
                isDirty = false;
                isMouseHovering = false;
                hasCustom = false;
                customRowItem = null;
            }
        }

        internal sealed class Resources
        {
            private static Texture2D pixelWhite;

            public static Texture2D PixelWhite
            {
                get
                {
                    if (pixelWhite == null)
                    {
                        pixelWhite = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                        pixelWhite.SetPixel(0, 0, Color.white);
                        pixelWhite.Apply();
                    }

                    return pixelWhite;
                }
            }

            private static Texture2D ramp8x8White;

            public static Texture2D Ramp8x8White
            {
                get
                {
                    if (ramp8x8White == null)
                    {
                        ramp8x8White = new byte[]
                        {
                            137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 16,
                            0, 0, 0, 16, 8, 6, 0, 0, 0, 31, 243, 255, 97, 0, 0, 0, 40, 73, 68, 65, 84, 56, 17, 99, 252,
                            15, 4, 12, 12,
                            12, 31, 8, 224, 143, 184, 228, 153, 128, 18, 20, 129, 81, 3, 24, 24, 70, 195, 96, 52, 12,
                            64, 153, 104, 224,
                            211, 1, 0, 153, 171, 18, 45, 165, 62, 165, 211, 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130
                        }.PNGImageDecode();
                    }

                    return ramp8x8White;
                }
            }

            private static Texture2D treeTRDIcon;

            public static Texture2D TreeTRDIcon
            {
                get
                {
                    if (treeTRDIcon == null)
                        treeTRDIcon = new byte[]
                        {
                            137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0,
                            35, 0, 0, 0, 35, 8, 6, 0, 0, 0, 30, 217, 179, 89, 0, 0, 0, 116, 73, 68, 65, 84, 88, 9, 237,
                            214, 177,
                            14, 128, 32, 12, 69, 81, 49, 254, 255, 47, 171, 65, 72, 238, 192, 91, 165, 195, 101, 225,
                            165, 11, 205,
                            105, 7, 218, 253, 158, 227, 59, 109, 220, 219, 174, 115, 219, 203, 139, 135, 109, 102, 129,
                            210, 75, 202, 40,
                            147, 4, 82, 221, 157, 81, 38, 9, 164, 186, 59, 163, 76, 18, 72, 245, 82, 59, 115, 161, 203,
                            249, 201, 66, 233, 223,
                            88, 86, 198, 111, 39, 23, 161, 212, 152, 108, 134, 163, 97, 86, 134, 26, 204, 202, 80, 131,
                            89, 25, 106, 48, 43, 67, 13,
                            102, 101, 168, 193, 92, 74, 230, 1, 149, 182, 5, 71, 91, 165, 185, 13, 0, 0, 0, 0, 73, 69,
                            78, 68, 174, 66, 96, 130
                        }.PNGImageDecode();
                    return treeTRDIcon;
                }
            }

            private static Texture2D treeTRIcon;

            public static Texture2D TreeTRIcon
            {
                get
                {
                    if (treeTRIcon == null)
                        treeTRIcon = new byte[]
                        {
                            137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 35,
                            0, 0, 0, 35, 8, 6, 0, 0, 0, 30, 217, 179, 89, 0, 0, 0, 119, 73, 68, 65, 84, 88, 9, 237, 214,
                            49, 14, 128,
                            48, 12, 67, 81, 138, 184, 255, 149, 1, 1, 150, 42, 171, 94, 73, 134, 223, 197, 36, 75, 163,
                            71, 134, 142, 243,
                            62, 219, 123, 198, 151, 101, 177, 151, 221, 188, 184, 152, 97, 22, 40, 79, 11, 25, 100, 146,
                            64, 234, 179, 51, 200,
                            36, 129, 212, 103, 103, 144, 73, 2, 169, 223, 106, 103, 142, 105, 74, 61, 178, 166, 214,
                            191, 159, 237, 100, 202, 159,
                            155, 242, 111, 37, 195, 48, 250, 45, 158, 200, 184, 136, 106, 100, 36, 225, 137, 140, 139,
                            168, 70, 70, 18, 158, 200, 184,
                            136, 106, 100, 36, 225, 121, 1, 149, 190, 5, 71, 8, 114, 89, 2, 0, 0, 0, 0, 73, 69, 78, 68,
                            174, 66, 96, 130
                        }.PNGImageDecode();
                    return treeTRIcon;
                }
            }

            private static Texture2D treeTDIcon;

            public static Texture2D TreeTDIcon
            {
                get
                {
                    if (treeTDIcon == null)
                        treeTDIcon = new byte[]
                        {
                            137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 35, 0,
                            0, 0, 35, 8, 6, 0, 0, 0, 30, 217, 179, 89, 0, 0, 0, 90, 73, 68, 65, 84, 88, 9, 237, 210, 65,
                            10, 128, 32,
                            0, 69, 193, 236, 254, 119, 174, 200, 150, 190, 181, 4, 227, 70, 120, 27, 101, 248, 227, 122,
                            206, 49, 207,
                            248, 238, 109, 215, 185, 237, 229, 197, 195, 62, 179, 64, 121, 19, 25, 50, 37, 80, 221, 102,
                            200, 148, 64, 117,
                            155, 33, 83, 2, 213, 109, 134, 76, 9, 84, 183, 25, 50, 37, 80, 221, 102, 200, 148, 64, 117,
                            155, 33, 83, 2, 213,
                            109, 230, 23, 50, 55, 146, 198, 4, 67, 142, 224, 65, 199, 0, 0, 0, 0, 73, 69, 78, 68, 174,
                            66, 96, 130
                        }.PNGImageDecode();
                    return treeTDIcon;
                }
            }

            internal static readonly Texture lockIconOn = EditorGUIUtility.IconContent("LockIcon-On").image;
        }

        internal static class Styles
        {
            internal static GUIStyle lineStyle = new GUIStyle("TV Line");

            internal static GUIStyle PR_DisabledLabel = new GUIStyle("PR DisabledLabel");

            internal static GUIStyle PR_PrefabLabel = new GUIStyle("PR PrefabLabel");

            internal static GUIStyle PR_DisabledPrefabLabel = new GUIStyle("PR DisabledPrefabLabel");

            internal static GUIStyle PR_BrokenPrefabLabel = new GUIStyle("PR BrokenPrefabLabel");

            internal static GUIStyle PR_DisabledBrokenPrefabLabel = new GUIStyle("PR DisabledBrokenPrefabLabel");

            internal static GUIStyle Tag = new GUIStyle()
            {
                padding = new RectOffset(3, 4, 0, 0),
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Italic,
                fontSize = 8,
                richText = true,
                border = new RectOffset(12, 12, 8, 8),
            };

            internal static GUIStyle Layer = new GUIStyle()
            {
                padding = new RectOffset(3, 4, 0, 0),
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Italic,
                fontSize = 8,
                richText = true,
                border = new RectOffset(12, 12, 8, 8),
            };

            [System.Obsolete] internal static GUIStyle DirtyLabel = new GUIStyle(EditorStyles.label)
            {
                padding = new RectOffset(-1, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(0, 0, 0, 0),
                alignment = TextAnchor.UpperLeft,
            };

            internal static GUIStyle Header = new GUIStyle(TreeBoldLabel)
            {
                richText = true,
                normal = new GUIStyleState() {textColor = Color.white}
            };

            internal static GUIStyle TreeBoldLabel
            {
                get { return TreeView.DefaultStyles.boldLabel; }
            }

            internal static GUIStyle TreeLabel
            {
                get { return TreeView.DefaultStyles.label; }
            }
        }

        internal sealed class MenuCommand
        {
            const int priority = 200;

            [MenuItem("GameObject/Lock Selection %l", false, priority)]
            static void SetNotEditableObject()
            {
                Undo.RegisterCompleteObjectUndo(Selection.gameObjects, "Set Selections Flag NotEditable");
                for (int i = 0; i < Selection.gameObjects.Length; ++i)
                {
                    Selection.gameObjects[i].hideFlags = HideFlags.NotEditable;
                    EditorUtility.SetDirty(Selection.gameObjects[i]);
                }
            }

            [MenuItem("GameObject/Lock Selection %l", true, priority)]
            static bool ValidateSetNotEditableObject() => Selection.gameObjects.Length > 0;

            [MenuItem("GameObject/Unlock Selection %&l", false, priority)]
            static void SetEditableObject()
            {
                Undo.RegisterCompleteObjectUndo(Selection.gameObjects, "Set Selections Flag Editable");
                for (int i = 0; i < Selection.gameObjects.Length; ++i)
                {
                    Selection.gameObjects[i].hideFlags = HideFlags.None;
                    EditorUtility.SetDirty(Selection.gameObjects[i]);
                }
            }

            [MenuItem("GameObject/Unlock Selection %&l", true, priority)]
            static bool ValidateSetEditableObject() => Selection.gameObjects.Length > 0;


            [MenuItem("GameObject/Move Selection Up #w", false, priority)]
            static void QuickSiblingUp()
            {
                var gameObject = Selection.activeGameObject;
                if (gameObject == null)
                    return;

                var index = gameObject.transform.GetSiblingIndex();
                if (index > 0)
                {
                    Undo.RegisterCompleteObjectUndo(gameObject, string.Format("{0} Parenting", gameObject.name));

                    gameObject.transform.SetSiblingIndex(--index);
                    if (!EditorApplication.isPlaying)
                        EditorSceneManager.MarkSceneDirty(gameObject.scene);
                }
            }

            [MenuItem("GameObject/Move Selection Up #w", true)]
            static bool ValidateQuickSiblingUp() => Selection.activeTransform != null;

            [MenuItem("GameObject/Move Selection Down #s", false, priority)]
            static void QuickSiblingDown()
            {
                var gameObject = Selection.activeGameObject;
                if (gameObject == null)
                    return;

                Undo.RegisterCompleteObjectUndo(gameObject, string.Format("{0} Parenting", gameObject.name));

                var index = gameObject.transform.GetSiblingIndex();
                gameObject.transform.SetSiblingIndex(++index);
                if (!EditorApplication.isPlaying)
                    EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }

            [MenuItem("GameObject/Move Selection Down #s", true, priority)]
            static bool ValidateQuickSiblingDown() => Selection.activeTransform != null;
        }
    }
}