namespace ListaMateriais;

/// <summary>An expected failure whose message is written for the user and shown as is.</summary>
internal sealed class AppException(string message, Exception? inner = null) : Exception(message, inner);
