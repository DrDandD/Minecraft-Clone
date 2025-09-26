using UnityEngine;
using UnityEngine.UI;

public class SimpleHotbarUI : MonoBehaviour
{
    public Hotbar hotbar;
    public Text label;
    void Update()
    {
        if (hotbar && label) label.text = $"Slot: {hotbar.selected + 1}  Block: {hotbar.SelectedBlock}";
    }
}
