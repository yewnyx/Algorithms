using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Yewnyx.Packables;

/// <summary>One registered type: the name documents use for it, and the version of its layout.</summary>
public sealed class PackableRegistration {
    public Type Type { get; }
    public string Name { get; }
    public int Version { get; }

    /// <summary>
    /// Earlier names: old assembly-qualified type names, or earlier ids. The format 1 converter
    /// matches them exactly, whole string; nothing else looks them up yet.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>"Name#Version", as documents write it.</summary>
    public string VersionedName { get; }

    internal PackableRegistration(Type type, string name, int version, string[] aliases) {
        Type = type;
        Name = name;
        Version = version;
        Aliases = new ReadOnlyCollection<string>(aliases);
        VersionedName = PackableNames.Format(name, version);
    }
}

/// <summary>
/// The types documents may hold, by name and version. Immutable once built; build a new one to replace
/// it. It holds data only: creating objects is the job of the type doing the unpacking.
/// </summary>
public sealed class PackableRegistry {
    private readonly Dictionary<Type, PackableRegistration> _byType;
    private readonly Dictionary<string, PackableRegistration> _byName;

    public static PackableRegistry Empty { get; } = new Builder().Build();

    /// <summary>In the order they were added.</summary>
    public IReadOnlyList<PackableRegistration> Registrations { get; }

    private PackableRegistry(List<PackableRegistration> registrations) {
        Registrations = new ReadOnlyCollection<PackableRegistration>(registrations.ToArray());
        _byType = new Dictionary<Type, PackableRegistration>(registrations.Count);
        _byName = new Dictionary<string, PackableRegistration>(registrations.Count, StringComparer.Ordinal);
        foreach (var registration in registrations) {
            _byType.Add(registration.Type, registration);
            _byName.Add(registration.Name, registration);
        }
    }

    public bool TryGet(Type type, [NotNullWhen(true)] out PackableRegistration? registration) =>
        _byType.TryGetValue(type, out registration);

    public bool TryGet<T>([NotNullWhen(true)] out PackableRegistration? registration) where T : class, IPackable =>
        _byType.TryGetValue(typeof(T), out registration);

    /// <summary>Looks up a registered name, without its version.</summary>
    public bool TryGet(string name, [NotNullWhen(true)] out PackableRegistration? registration) =>
        _byName.TryGetValue(name, out registration);

    public sealed class Builder {
        private readonly List<PackableRegistration> _registrations = new();

        /// <summary>
        /// Registers <typeparamref name="T"/> as <paramref name="name"/> at <paramref name="version"/>,
        /// as <see cref="Add(Type, string, int, string[])"/> does.
        /// </summary>
        public Builder Add<T>(string name, int version, params string[] aliases) where T : class, IPackable =>
            Add(typeof(T), name, version, aliases);

        /// <summary>
        /// Registers <paramref name="type"/> as <paramref name="name"/> at <paramref name="version"/>.
        /// Throws if the type isn't a concrete packable class, the name breaks the naming rules, an alias
        /// is empty, or the type, name or an old name is already taken.
        /// </summary>
        public Builder Add(Type type, string name, int version, params string[] aliases) {
            if (type is null) { throw new ArgumentNullException(nameof(type)); }
            if (type.IsValueType) {
                // Each ref would box a new copy, so a struct could never be shared, looped or unpacked.
                throw new ArgumentException($"{_describe(type)} is a struct; packables are classes.", nameof(type));
            }
            if (!typeof(IPackable).IsAssignableFrom(type)) {
                throw new ArgumentException($"{_describe(type)} doesn't implement IPackable.", nameof(type));
            }
            if (type.IsAbstract || type.ContainsGenericParameters) {
                // Objects are looked up by their exact type, which is never abstract, an interface or open.
                throw new ArgumentException($"{_describe(type)} can't be an object's type; register the concrete types.", nameof(type));
            }
            if (!PackableNames.IsValidName(name)) { throw new ArgumentException($"\"{name}\" isn't a valid name.", nameof(name)); }
            if (version < 0) { throw new ArgumentOutOfRangeException(nameof(version), version, "Versions start at 0."); }
            foreach (var alias in aliases) {
                if (string.IsNullOrEmpty(alias)) {
                    throw new ArgumentException("Aliases can't be empty.", nameof(aliases));
                }
            }
            foreach (var existing in _registrations) {
                if (existing.Type == type) { throw new ArgumentException($"{_describe(type)} is already registered.", nameof(type)); }
                if (existing.Name == name) {
                    throw new ArgumentException(
                        $"\"{name}\" is registered by both {_describe(existing.Type)} and {_describe(type)}.", nameof(name));
                }
                // An old name, whether an alias or a type's own assembly-qualified name, maps to one registration.
                foreach (var claimed in _oldNames(existing.Type, existing.Aliases)) {
                    foreach (var mine in _oldNames(type, aliases)) {
                        if (claimed == mine) {
                            throw new ArgumentException(
                                $"\"{mine}\" is claimed by both {_describe(existing.Type)} and {_describe(type)}.", nameof(aliases));
                        }
                    }
                }
            }

            _registrations.Add(new PackableRegistration(type, name, version, (string[])aliases.Clone()));
            return this;
        }

        /// <summary>A registry of everything added so far. Later additions don't change it.</summary>
        public PackableRegistry Build() => new(_registrations);

        private static string _describe(Type type) => $"{type.FullName} ({type.Assembly.GetName().Name})";

        private static IEnumerable<string> _oldNames(Type type, IEnumerable<string> aliases) {
            if (type.AssemblyQualifiedName is { } own) { yield return own; }
            foreach (var alias in aliases) { yield return alias; }
        }
    }
}

/// <summary>
/// Names in documents: a registered name is letters, digits, '.', '-' and '_', and documents write it
/// with its version as "Name#Version", the version being a whole number from 0 without leading zeros.
/// </summary>
public static class PackableNames {
    public static bool IsValidName(string? name) {
        if (string.IsNullOrEmpty(name)) { return false; }
        foreach (var c in name!) {
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_')) {
                return false;
            }
        }
        return true;
    }

    public static string Format(string name, int version) => $"{name}#{version}";

    /// <summary>Splits "Name#Version" at its last '#'. False if either part breaks the rules.</summary>
    public static bool TryParse(string? versionedName, [NotNullWhen(true)] out string? name, out int version) {
        (name, version) = (null, 0);
        var split = versionedName?.LastIndexOf('#') ?? -1;
        if (split < 0) { return false; }

        var digits = versionedName!.AsSpan(split + 1);
        if (digits.Length == 0 || (digits.Length > 1 && digits[0] == '0')) { return false; }
        var value = 0L;
        foreach (var c in digits) {
            if (c is < '0' or > '9') { return false; }
            value = value * 10 + (c - '0');
            if (value > int.MaxValue) { return false; }
        }

        var candidate = versionedName!.Substring(0, split);
        if (!IsValidName(candidate)) { return false; }
        (name, version) = (candidate, (int)value);
        return true;
    }
}
