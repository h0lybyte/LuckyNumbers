using Object = UnityEngine.Object;

namespace LuckyNumbers.Core
{
    /// <summary>
    /// An Addressables address and the type behind it. Don't write these by hand: <see cref="AssetKeys"/> is
    /// generated from the Addressables settings (Editor/AddressablesSetup), so a renamed or removed asset is a
    /// compile error instead of a null on the web build.
    /// </summary>
    public readonly struct AssetKey<T> where T : Object
    {
        public readonly string Address;
        public AssetKey(string address) => Address = address;
        public override string ToString() => Address;
    }
}
