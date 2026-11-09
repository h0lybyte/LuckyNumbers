using UnityEngine;

namespace LuckyNumbers.Planet
{
    /// <summary>The player body (CharacterController) and head (camera holder).</summary>
    [View]
    public sealed class PlayerView : MonoBehaviour
    {
        [SerializeField] CharacterController body;
        [SerializeField] Transform head;
        [SerializeField] Camera eyes;

        public CharacterController Body => body;
        public Transform Head => head;
        public Camera Camera => eyes;
    }
}
