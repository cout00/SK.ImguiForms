using System;

[assembly: EncryptExclude]

[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false)]
sealed class EncryptExcludeAttribute : Attribute {
}
