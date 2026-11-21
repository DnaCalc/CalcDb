using CalcDb.Com.Declarations;

namespace CalcDb.Com.Com;

/// <summary>
/// Lookups over the generated declarations: interfaces by name within their own library, coclass default interfaces,
/// base chains and the types a USERDEFINED name stands for. DAO and ADO share names (Properties, Errors, Field,
/// Recordset): a name is always resolved in the library of the declaration that uses it (W-09/construction-policy).
/// </summary>
public static class DeclarationIndex
{
    private static readonly Lock Gate = new();
    private static volatile LibraryDecl[] _libraries = [];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<InterfaceDecl, LibraryDecl> Owners = new(ReferenceEqualityComparer.Instance);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<LibraryDecl, Dictionary<string, InterfaceDecl>> ByName = new(ReferenceEqualityComparer.Instance);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<InterfaceDecl, Guid[]> Chains = new(ReferenceEqualityComparer.Instance);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<LibraryDecl, System.Collections.Concurrent.ConcurrentDictionary<string, string>> Resolved = new(ReferenceEqualityComparer.Instance);

    /// <summary>The libraries CalcDb.dll implements.</summary>
    public static IReadOnlyList<LibraryDecl> Libraries => _libraries;

    /// <summary>Register a generated library declaration (idempotent, thread-safe).</summary>
    public static void Register(LibraryDecl library)
    {
        if (ByName.ContainsKey(library))
        {
            return;
        }
        lock (Gate)
        {
            if (ByName.ContainsKey(library))
            {
                return;
            }
            foreach (InterfaceDecl i in library.Interfaces)
            {
                Owners[i] = library;
            }
            Resolved[library] = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            ByName[library] = library.Interfaces.ToDictionary(i => i.Name, StringComparer.Ordinal);
            _libraries = [.. _libraries, library];
        }
    }

    /// <summary>The library that declares <paramref name="declaration"/>.</summary>
    public static LibraryDecl LibraryOf(InterfaceDecl declaration) => Owners[declaration];

    /// <summary>The interface a declared type name stands for in <paramref name="library"/>: the interface, or a coclass's default interface.</summary>
    public static InterfaceDecl? Interface(LibraryDecl library, string name)
    {
        Dictionary<string, InterfaceDecl> byName = ByName[library];
        if (byName.TryGetValue(name, out InterfaceDecl? i))
        {
            return i;
        }
        CoclassDecl? coclass = library.Coclasses.FirstOrDefault(c => c.Name == name);
        string? defaultInterface = coclass?.Implements.FirstOrDefault(x => (x.ImplFlags & 1) != 0).Interface;
        return defaultInterface is not null && byName.TryGetValue(defaultInterface, out InterfaceDecl? d) ? d : null;
    }

    /// <summary>The IIDs of a declaration and its bases, built once per declaration (QueryInterface asks on every call; task hdw6).</summary>
    public static Guid[] Chain(InterfaceDecl declaration) => Chains.GetOrAdd(declaration, static d =>
    {
        Dictionary<string, InterfaceDecl> byName = ByName[LibraryOf(d)];
        var chain = new List<Guid>();
        for (InterfaceDecl? i = d; i is not null; i = byName.GetValueOrDefault(i.BaseName))
        {
            chain.Add(i.Iid);
        }
        return [.. chain];
    });

    /// <summary>
    /// The type text a parameter or return type of <paramref name="declaration"/> stands for on the vtable: an enum of
    /// its library is VT_I4, an alias its target (resolved again), any other type itself. Resolved once per type text.
    /// </summary>
    public static string Scalar(InterfaceDecl declaration, string type)
    {
        if (!type.StartsWith("USERDEFINED ", StringComparison.Ordinal))
        {
            return type;
        }
        LibraryDecl library = LibraryOf(declaration);
        return Resolved[library].GetOrAdd(type, static (t, l) => ResolveIn(l, t), library);
    }

    private static string ResolveIn(LibraryDecl library, string type)
    {
        for (int guard = 0; guard < 8 && type.StartsWith("USERDEFINED ", StringComparison.Ordinal); guard++)
        {
            string name = type[12..];
            if (library.Enums.Any(e => e.Name == name))
            {
                return "VT_I4";
            }
            AliasDecl? alias = library.AliasList.FirstOrDefault(a => a.Name == name);
            if (alias is null)
            {
                return type;
            }
            type = alias.Target;
        }
        return type;
    }
}
