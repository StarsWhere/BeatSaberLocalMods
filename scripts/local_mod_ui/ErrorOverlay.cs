using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using HMUI;
using UnityEngine;

namespace LocalModUI
{
    internal sealed class ErrorOverlay : MonoBehaviour
    {
        [UIComponent("error-modal")] private ModalView modal;
        public string Message { get; private set; }
        private float deadline;
        internal void Show(string message)
        {
            Message = message;
            BSMLParser.Instance.Parse("<modal id='error-modal' size-delta-x='100' size-delta-y='35' move-to-center='true' click-off-closes='true'><vertical pad='3' spacing='2'><text text='~Message' rich-text='false' word-wrapping='true' font-size='3.2' preferred-height='17'/><button text='Close' on-click='close' pref-height='8' font-size='3.2'/></vertical></modal>", gameObject, this);
            deadline = Time.realtimeSinceStartup + 10;
            modal.Show(true, true, null);
        }
        [UIAction("close")] private void Close() { if (modal != null) modal.Hide(false); Destroy(gameObject); }
        private void Update() { if (Time.realtimeSinceStartup >= deadline) Close(); }
        private void OnDisable() { ReleaseModal(); }
        private void OnDestroy() { ReleaseModal(); }
        private void ReleaseModal()
        {
            // ModalView reparents itself while shown; it is still owned here.
            var owned = modal;
            modal = null;
            if (owned != null) { owned.Hide(false); Destroy(owned.gameObject); }
        }
    }
}
