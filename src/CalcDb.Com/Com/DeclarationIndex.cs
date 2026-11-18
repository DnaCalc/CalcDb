using CalcDb.Com.Declarations;

namespace CalcDb.Com.Com;

/// <summary>Lookups over the generated declarations: interfaces by name, coclass default interfaces and base chains.</summary>
internal static class DeclarationIndex
{
    private static readonly Dictionary<string, InterfaceDecl> ByName =
        DaoTypes.Library.Interfaces.ToDictionary(i => i.Name, StringComparer.Ordinal);

    /// <summary>The interface a declared type name stands for: the interface, or a coclass's default interface.</summary>
    public static InterfaceDecl? Interface(string name)
    {
        if (ByName.TryGetValue(name, out InterfaceDecl? i))
        {
            return i;
        }
        CoclassDecl? coclass = DaoTypes.Library.Coclasses.FirstOrDefault(c => c.Name == name);
        string? defaultInterface = coclass?.Implements.FirstOrDefault(x => (x.ImplFlags & 1) != 0).Interface;
        return defaultInterface is not null && ByName.TryGetValue(defaultInterface, out InterfaceDecl? d) ? d : null;
    }

    /// <summary>The IIDs of an interface and of every library interface it derives from (all answered by one head).</summary>
    /// <summary>The IIDs of a declaration and its bases, built once per declaration (QueryInterface asks on every call; task hdw6).</summary>
    public static Guid[] Chain(InterfaceDecl declaration) => Chains.GetOrAdd(declaration, static d =>
    {
        var chain = new List<Guid>();
        for (InterfaceDecl? i = d; i is not null; i = ByName.GetValueOrDefault(i.BaseName))
        {
            chain.Add(i.Iid);
        }
        return [.. chain];
    });

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<InterfaceDecl, Guid[]> Chains = new(ReferenceEqualityComparer.Instance);
}
