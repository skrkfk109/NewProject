using UnityEngine;

namespace LobbyRelaySample.vivox
{
    /// <summary>
    /// Compatibility placeholder for the original sample's optional Vivox UI.
    /// Voice chat is intentionally not included in this prototype, but retaining
    /// the component keeps the lobby card prefabs free of missing-script errors.
    /// </summary>
    public class VivoxUserHandler : MonoBehaviour
    {
        [SerializeField] UI.LobbyUserVolumeUI m_lobbyUserVolumeUI;

        public static float NormalizedVolumeDefault => 50f / 70f;

        void Start()
        {
            if (m_lobbyUserVolumeUI != null)
                m_lobbyUserVolumeUI.DisableVoice(true);
        }

        public void SetId(string id) { }
        public void OnVolumeSlide(float volumeNormalized) { }
        public void OnMuteToggle(bool isMuted) { }
    }
}
