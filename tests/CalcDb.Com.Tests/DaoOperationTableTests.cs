using System.Runtime.CompilerServices;
using CalcDb.Com.Dao;

namespace CalcDb.Com.Tests;

/// <summary>
/// Every DAO wrapper's operation table binds each operation to a declared member of its generated declaration
/// (Declarations/Dao.g.cs) when its class initializes; a name the declaration lacks would fail every call of that class.
/// </summary>
public sealed class DaoOperationTableTests
{
    public static TheoryData<Type> Wrappers => new()
    {
        typeof(DBEngineObject), typeof(WorkspaceObject), typeof(WorkspacesObject), typeof(DatabasesObject), typeof(EmptyCollection),
        typeof(DatabaseObject), typeof(DefinitionCollection), typeof(TableDefObject), typeof(IndexObject), typeof(DefinitionFieldObject),
        typeof(QueryDefObject), typeof(ParameterObject), typeof(PropertiesObject), typeof(PropertyObject), typeof(RecordsetObject),
        typeof(FieldObject), typeof(ContainerObject), typeof(DocumentObject), typeof(RelationObject), typeof(RelationFieldObject),
    };

    [Theory]
    [MemberData(nameof(Wrappers))]
    public void The_operation_table_binds_to_declared_members(Type wrapper) => RuntimeHelpers.RunClassConstructor(wrapper.TypeHandle);

    [Fact]
    public void Recordset_options_follow_the_option_matrix()
    {
        // r-cursor-1b/option-flags-by-type and r-fix-cursor-dao/option-flags-rw (qualified.md DAO-UPDATABLE).
        Assert.Equal(OptionAdmission.InvalidArgument, RecordsetOptions.Admit(1, 1024));
        Assert.Equal(OptionAdmission.InvalidArgument, RecordsetOptions.Admit(4, 48));
        Assert.Equal(OptionAdmission.InvalidArgument, RecordsetOptions.Admit(2, 64));
        Assert.Equal(OptionAdmission.Admitted, RecordsetOptions.Admit(1, 260));
        Assert.Equal(OptionAdmission.NotModelled, RecordsetOptions.Admit(2, 8));
        Assert.True(RecordsetOptions.Updatable(1, 256, readOnlyDatabase: false));
        Assert.False(RecordsetOptions.Updatable(2, 256, readOnlyDatabase: false));
        Assert.False(RecordsetOptions.Updatable(1, 12, readOnlyDatabase: false));
        Assert.False(RecordsetOptions.Updatable(1, 0, readOnlyDatabase: true));
        Assert.False(RecordsetOptions.Bookmarkable(4, 260));
        Assert.True(RecordsetOptions.Bookmarkable(1, 256));
    }
}
