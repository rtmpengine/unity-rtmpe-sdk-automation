namespace RTMPE.SDK.Conversion.Core
{
    /// <summary>
    /// The closed set of parameter types <c>RpcSerializer</c> encodes, keyed by
    /// the unqualified source spelling — the RPC analog of
    /// <see cref="NetworkVariableTypeMap"/>, and deliberately wider than it
    /// (byte[], ulong, Color have wire tags; there is no NetworkVariable for
    /// them). <c>INetworkSerializable</c> implementers are excluded on purpose:
    /// membership is a semantic fact a syntax-scoped pass cannot prove, and an
    /// unregistered implementer arrives null at the receiver, so the headless
    /// engine refuses such a parameter rather than emit a silent null-drop.
    /// </summary>
    public static class RpcParameterClassifier
    {
        /// <summary>
        /// True when a parameter whose type is spelled
        /// <paramref name="parameterTypeName"/> (unqualified; arrays in
        /// <c>element[]</c> form) is encodable by the runtime serializer.
        /// </summary>
        public static bool IsSupported(string parameterTypeName)
        {
            switch (parameterTypeName)
            {
                case "int":
                case "float":
                case "bool":
                case "string":
                case "ulong":
                case "byte[]":
                case "Vector3":
                case "Color":
                case "Quaternion":
                    return true;
                default:
                    return false;
            }
        }
    }
}
