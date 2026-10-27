using UnityEngine;
using TMPro;

// The kit's BuildInfo asset went with OpenMMORPG v1.1.4; the player settings' version is the one shown.
[RequireComponent(typeof(TextMeshProUGUI))]
public class VersionDisplay : MonoBehaviour
{
    private void Start()
    {
        GetComponent<TextMeshProUGUI>().text = $"v{Application.version}";
    }
}
