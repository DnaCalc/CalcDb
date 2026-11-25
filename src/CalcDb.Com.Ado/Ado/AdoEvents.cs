using CalcDb.Com.Com;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Ado;

/// <summary>ADO <c>EventStatusEnum</c> values delivered in <c>adStatus</c>.</summary>
internal static class AdoEventStatus
{
    public const int Ok = 1;                // adStatusOK
    public const int ErrorsOccurred = 2;    // adStatusErrorsOccurred
    public const int CantDeny = 3;          // adStatusCantDeny
    public const int Cancel = 4;            // adStatusCancel
    public const int UnwantedEvent = 5;     // adStatusUnwantedEvent
}

/// <summary>ADO <c>EventReasonEnum</c> values delivered in <c>adReason</c>.</summary>
internal static class AdoEventReason
{
    public const int AddNew = 1;            // adRsnAddNew
    public const int Delete = 2;            // adRsnDelete
    public const int Update = 3;            // adRsnUpdate
    public const int UndoUpdate = 4;        // adRsnUndoUpdate
    public const int UndoAddNew = 5;        // adRsnUndoAddNew
    public const int Requery = 7;           // adRsnRequery
    public const int Resynch = 8;           // adRsnResynch
    public const int Close = 9;             // adRsnClose
    public const int Move = 10;             // adRsnMove
    public const int FirstChange = 11;      // adRsnFirstChange
    public const int MoveFirst = 12;        // adRsnMoveFirst
    public const int MoveNext = 13;         // adRsnMoveNext
    public const int MovePrevious = 14;     // adRsnMovePrevious
    public const int MoveLast = 15;         // adRsnMoveLast
}

/// <summary>
/// Typed synchronous emitters for ADO <c>ConnectionEvents</c> (<c>{00001400-0000-0010-8000-00AA006D2EA4}</c>) and
/// <c>RecordsetEvents</c> (<c>{00001266-0000-0010-8000-00AA006D2EA4}</c>) (W-09/events-persistence-inventory;
/// R-ADO-1 ADO-EVENT, R-ACT-1 ACT-THREAD). Every wire VARTYPE, byRef flag and DISPID matches ACE's retained records.
/// </summary>
internal static unsafe class AdoEvents
{
    // ConnectionEvents dispids (AdoTypes.ConnectionEventsMeta)
    public const int DispIdBeginTransComplete = 1;
    public const int DispIdRollbackTransComplete = 2;
    public const int DispIdCommitTransComplete = 3;
    public const int DispIdWillExecute = 4;
    public const int DispIdExecuteComplete = 5;
    public const int DispIdWillConnect = 6;
    public const int DispIdConnectComplete = 7;
    public const int DispIdDisconnect = 8;

    // RecordsetEvents dispids (AdoTypes.RecordsetEventsMeta)
    public const int DispIdWillChangeField = 9;
    public const int DispIdFieldChangeComplete = 10;
    public const int DispIdWillChangeRecord = 11;
    public const int DispIdRecordChangeComplete = 12;
    public const int DispIdWillChangeRecordset = 13;
    public const int DispIdRecordsetChangeComplete = 14;
    public const int DispIdWillMove = 15;
    public const int DispIdMoveComplete = 16;
    public const int DispIdEndOfRecordset = 17;

    // ---- ConnectionEvents ----

    public static void BeginTransComplete(ConnectionPoint point, int transactionLevel, AdoErrorObject? error, int status, ConnectionObject connection)
    {
        if (!point.HasSinks)
        {
            return;
        }
        int adStatus = status;
        Span<NativeVariant> args = stackalloc NativeVariant[4];
        args[0] = Int32(transactionLevel);
        args[1] = Unknown(error);
        args[2] = ByRefInt32(&adStatus);
        args[3] = Unknown(connection);
        point.Invoke(DispIdBeginTransComplete, args, &adStatus);
    }

    public static void RollbackTransComplete(ConnectionPoint point, AdoErrorObject? error, int status, ConnectionObject connection)
    {
        if (!point.HasSinks)
        {
            return;
        }
        int adStatus = status;
        Span<NativeVariant> args = stackalloc NativeVariant[3];
        args[0] = Unknown(error);
        args[1] = ByRefInt32(&adStatus);
        args[2] = Unknown(connection);
        point.Invoke(DispIdRollbackTransComplete, args, &adStatus);
    }

    public static void CommitTransComplete(ConnectionPoint point, AdoErrorObject? error, int status, ConnectionObject connection)
    {
        if (!point.HasSinks)
        {
            return;
        }
        int adStatus = status;
        Span<NativeVariant> args = stackalloc NativeVariant[3];
        args[0] = Unknown(error);
        args[1] = ByRefInt32(&adStatus);
        args[2] = Unknown(connection);
        point.Invoke(DispIdCommitTransComplete, args, &adStatus);
    }

    public static void WillConnect(
        ConnectionPoint point,
        string? connectionString,
        string? userId,
        string? password,
        int options,
        ConnectionObject connection)
    {
        if (!point.HasSinks)
        {
            return;
        }
        nint bstrConn = AllocOptionalBstr(connectionString);
        nint bstrUser = AllocOptionalBstr(userId);
        nint bstrPass = AllocOptionalBstr(password);
        int opt = options;
        int adStatus = AdoEventStatus.Ok;
        try
        {
            Span<NativeVariant> args = stackalloc NativeVariant[6];
            args[0] = ByRefBstr(&bstrConn);
            args[1] = ByRefBstr(&bstrUser);
            args[2] = ByRefBstr(&bstrPass);
            args[3] = ByRefInt32(&opt);
            args[4] = ByRefInt32(&adStatus);
            args[5] = Unknown(connection);
            point.Invoke(DispIdWillConnect, args, &adStatus);
        }
        finally
        {
            FreeIfNonZero(bstrConn);
            FreeIfNonZero(bstrUser);
            FreeIfNonZero(bstrPass);
        }
    }

    public static void ConnectComplete(ConnectionPoint point, AdoErrorObject? error, int status, ConnectionObject connection)
    {
        if (!point.HasSinks)
        {
            return;
        }
        int adStatus = status;
        Span<NativeVariant> args = stackalloc NativeVariant[3];
        args[0] = Unknown(error);
        args[1] = ByRefInt32(&adStatus);
        args[2] = Unknown(connection);
        point.Invoke(DispIdConnectComplete, args, &adStatus);
    }

    public static void Disconnect(ConnectionPoint point, ConnectionObject connection)
    {
        if (!point.HasSinks)
        {
            return;
        }
        int adStatus = AdoEventStatus.Ok;
        Span<NativeVariant> args = stackalloc NativeVariant[2];
        args[0] = ByRefInt32(&adStatus);
        args[1] = Unknown(connection);
        point.Invoke(DispIdDisconnect, args, &adStatus);
    }

    public static void WillExecute(
        ConnectionPoint point,
        string source,
        int cursorType,
        int lockType,
        int options,
        CommandObject? command,
        RecordsetObject? recordset,
        ConnectionObject connection)
    {
        if (!point.HasSinks)
        {
            return;
        }
        nint bstrSource = AllocOptionalBstr(source);
        int ct = cursorType;
        int lt = lockType;
        int opt = options;
        int adStatus = AdoEventStatus.Ok;
        try
        {
            Span<NativeVariant> args = stackalloc NativeVariant[8];
            args[0] = ByRefBstr(&bstrSource);
            args[1] = ByRefInt32(&ct);
            args[2] = ByRefInt32(&lt);
            args[3] = ByRefInt32(&opt);
            args[4] = ByRefInt32(&adStatus);
            args[5] = Unknown(command);
            args[6] = Unknown(recordset);
            args[7] = Unknown(connection);
            point.Invoke(DispIdWillExecute, args, &adStatus);
        }
        finally
        {
            FreeIfNonZero(bstrSource);
        }
    }

    public static void ExecuteComplete(
        ConnectionPoint point,
        long recordsAffected,
        AdoErrorObject? error,
        int status,
        CommandObject? command,
        RecordsetObject? recordset,
        ConnectionObject connection)
    {
        if (!point.HasSinks)
        {
            return;
        }
        int adStatus = status;
        Span<NativeVariant> args = stackalloc NativeVariant[6];
        args[0] = Int64(recordsAffected);
        args[1] = Unknown(error);
        args[2] = ByRefInt32(&adStatus);
        args[3] = Unknown(command);
        args[4] = Unknown(recordset);
        args[5] = Unknown(connection);
        point.Invoke(DispIdExecuteComplete, args, &adStatus);
    }

    // ---- RecordsetEvents ----

    public static void WillChangeField(ConnectionPoint point, AdoFieldObject field, RecordsetObject recordset)
    {
        if (!point.HasSinks)
        {
            return;
        }
        nint psa = CreateFieldArray(field);
        int adStatus = AdoEventStatus.Ok;
        try
        {
            Span<NativeVariant> args = stackalloc NativeVariant[4];
            args[0] = Int32(1);
            args[1] = DispatchArray(psa);
            args[2] = ByRefInt32(&adStatus);
            args[3] = Unknown(recordset);
            point.Invoke(DispIdWillChangeField, args, &adStatus);
        }
        finally
        {
            if (psa != 0)
            {
                OleAut.SafeArrayDestroy(psa);
            }
        }
    }

    public static void FieldChangeComplete(ConnectionPoint point, AdoFieldObject field, AdoErrorObject? error, int status, RecordsetObject recordset)
    {
        if (!point.HasSinks)
        {
            return;
        }
        nint psa = CreateFieldArray(field);
        int adStatus = status;
        try
        {
            Span<NativeVariant> args = stackalloc NativeVariant[5];
            args[0] = Int32(1);
            args[1] = DispatchArray(psa);
            args[2] = Unknown(error);
            args[3] = ByRefInt32(&adStatus);
            args[4] = Unknown(recordset);
            point.Invoke(DispIdFieldChangeComplete, args, &adStatus);
        }
        finally
        {
            if (psa != 0)
            {
                OleAut.SafeArrayDestroy(psa);
            }
        }
    }

    public static void WillChangeRecord(ConnectionPoint point, int reason, int recordCount, RecordsetObject recordset)
    {
        if (!point.HasSinks)
        {
            return;
        }
        int adStatus = AdoEventStatus.Ok;
        Span<NativeVariant> args = stackalloc NativeVariant[4];
        args[0] = Int32(reason);
        args[1] = Int32(recordCount);
        args[2] = ByRefInt32(&adStatus);
        args[3] = Unknown(recordset);
        point.Invoke(DispIdWillChangeRecord, args, &adStatus);
    }

    public static void RecordChangeComplete(ConnectionPoint point, int reason, int recordCount, AdoErrorObject? error, int status, RecordsetObject recordset)
    {
        if (!point.HasSinks)
        {
            return;
        }
        int adStatus = status;
        Span<NativeVariant> args = stackalloc NativeVariant[5];
        args[0] = Int32(reason);
        args[1] = Int32(recordCount);
        args[2] = Unknown(error);
        args[3] = ByRefInt32(&adStatus);
        args[4] = Unknown(recordset);
        point.Invoke(DispIdRecordChangeComplete, args, &adStatus);
    }

    public static void WillChangeRecordset(ConnectionPoint point, int reason, RecordsetObject recordset)
    {
        if (!point.HasSinks)
        {
            return;
        }
        int adStatus = AdoEventStatus.Ok;
        Span<NativeVariant> args = stackalloc NativeVariant[3];
        args[0] = Int32(reason);
        args[1] = ByRefInt32(&adStatus);
        args[2] = Unknown(recordset);
        point.Invoke(DispIdWillChangeRecordset, args, &adStatus);
    }

    public static void RecordsetChangeComplete(ConnectionPoint point, int reason, AdoErrorObject? error, int status, RecordsetObject recordset)
    {
        if (!point.HasSinks)
        {
            return;
        }
        int adStatus = status;
        Span<NativeVariant> args = stackalloc NativeVariant[4];
        args[0] = Int32(reason);
        args[1] = Unknown(error);
        args[2] = ByRefInt32(&adStatus);
        args[3] = Unknown(recordset);
        point.Invoke(DispIdRecordsetChangeComplete, args, &adStatus);
    }

    public static void WillMove(ConnectionPoint point, int reason, int status, RecordsetObject recordset)
    {
        if (!point.HasSinks)
        {
            return;
        }
        int adStatus = status;
        Span<NativeVariant> args = stackalloc NativeVariant[3];
        args[0] = Int32(reason);
        args[1] = ByRefInt32(&adStatus);
        args[2] = Unknown(recordset);
        point.Invoke(DispIdWillMove, args, &adStatus);
    }

    public static void MoveComplete(ConnectionPoint point, int reason, AdoErrorObject? error, int status, RecordsetObject recordset)
    {
        if (!point.HasSinks)
        {
            return;
        }
        int adStatus = status;
        Span<NativeVariant> args = stackalloc NativeVariant[4];
        args[0] = Int32(reason);
        args[1] = Unknown(error);
        args[2] = ByRefInt32(&adStatus);
        args[3] = Unknown(recordset);
        point.Invoke(DispIdMoveComplete, args, &adStatus);
    }

    public static void EndOfRecordset(ConnectionPoint point, RecordsetObject recordset)
    {
        if (!point.HasSinks)
        {
            return;
        }
        short moreData = 0; // VARIANT_FALSE
        int adStatus = AdoEventStatus.Ok;
        Span<NativeVariant> args = stackalloc NativeVariant[3];
        args[0] = ByRefBool(&moreData);
        args[1] = ByRefInt32(&adStatus);
        args[2] = Unknown(recordset);
        point.Invoke(DispIdEndOfRecordset, args, &adStatus);
    }

    // ---- VARIANT & SAFEARRAY helpers ----

    private static NativeVariant Int32(int value)
    {
        NativeVariant v = default;
        v.Vt = (ushort)VarType.I4;
        *(int*)&v.Int64 = value;
        return v;
    }

    private static NativeVariant Int64(long value)
    {
        NativeVariant v = default;
        v.Vt = (ushort)VarType.I8;
        v.Int64 = value;
        return v;
    }

    private static NativeVariant Unknown(ComObject? obj)
    {
        NativeVariant v = default;
        v.Vt = (ushort)VarType.Unknown;
        v.Pointer = obj is null ? 0 : obj.Identity;
        return v;
    }

    private static NativeVariant ByRefInt32(int* ptr)
    {
        NativeVariant v = default;
        v.Vt = (ushort)(VarType.ByRef | VarType.I4);
        v.Pointer = (nint)ptr;
        return v;
    }

    private static NativeVariant ByRefBool(short* ptr)
    {
        NativeVariant v = default;
        v.Vt = (ushort)(VarType.ByRef | VarType.Bool);
        v.Pointer = (nint)ptr;
        return v;
    }

    private static NativeVariant ByRefBstr(nint* ptr)
    {
        NativeVariant v = default;
        v.Vt = (ushort)(VarType.ByRef | VarType.Bstr);
        v.Pointer = (nint)ptr;
        return v;
    }

    private static NativeVariant DispatchArray(nint psa)
    {
        NativeVariant v = default;
        v.Vt = (ushort)(VarType.Array | VarType.Dispatch);
        v.Pointer = psa;
        return v;
    }

    private static nint AllocOptionalBstr(string? text) =>
        string.IsNullOrEmpty(text)
            ? 0
            : Bstrs.FromUnits(text.Length, text, static (dest, src) => src.AsSpan().CopyTo(dest));

    private static void FreeIfNonZero(nint bstr)
    {
        if (bstr != 0)
        {
            Bstrs.Free(bstr);
        }
    }

    private static nint CreateFieldArray(AdoFieldObject field)
    {
        SafeArrayBound bound = new() { Count = 1, Lower = 0 };
        nint psa = OleAut.SafeArrayCreate((ushort)VarType.Dispatch, 1, &bound);
        if (psa == 0)
        {
            return 0;
        }
        void* data = null;
        if (OleAut.SafeArrayAccessData(psa, &data) >= 0 && data is not null)
        {
            try
            {
                nint disp = field.Head(0);
                field.AddRef();
                *(nint*)data = disp;
            }
            finally
            {
                OleAut.SafeArrayUnaccessData(psa);
            }
        }
        return psa;
    }
}
