namespace BookSearch.Core;

/// <summary>
/// A model server could not be started, reached, or answered with an error. Core code catches this (to fall back to
/// keyword search, for example) without knowing anything about HTTP; only the Models project talks to the network.
/// </summary>
public sealed class ModelUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
