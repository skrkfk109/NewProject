#if UNITY_EDITOR
using System.Linq;
using ColorClash.Networking;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

public static class ColorClashLobbySceneSetup
{
    const string ScenePath = "Assets/Scenes/ColorClashLobby.unity";

    [MenuItem("Color Clash/Create or Open Lobby Scene")]
    public static void CreateOrOpenLobbyScene()
    {
        if (System.IO.File.Exists(ScenePath))
        {
            EditorSceneManager.OpenScene(ScenePath);
            return;
        }

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("Main Camera").AddComponent<Camera>();
        new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        var lobby = new GameObject("Color Clash Lobby");
        lobby.AddComponent<ColorClashLobbyController>();
        EditorSceneManager.SaveScene(scene, ScenePath);

        if (!EditorBuildSettings.scenes.Any(item => item.path == ScenePath))
            EditorBuildSettings.scenes = EditorBuildSettings.scenes
                .Append(new EditorBuildSettingsScene(ScenePath, true)).ToArray();
    }
}
#endif
