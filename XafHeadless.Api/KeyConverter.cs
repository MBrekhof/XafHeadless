namespace XafHeadless.Api;

// The demo model keys on Guid (BaseObject.ID); the original POC's model keyed on int. Convert.ChangeType
// handles int/long but NOT Guid (Guid is not IConvertible), so route string keys through here.
static class KeyConverter {
    public static object Convert(string key, Type keyType) =>
        keyType == typeof(Guid) ? Guid.Parse(key) : System.Convert.ChangeType(key, keyType);
}
