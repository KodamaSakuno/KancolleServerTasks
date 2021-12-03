namespace ServerAPI;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TransactionAttribute : Attribute
{
}
