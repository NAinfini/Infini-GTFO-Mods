namespace ForgeRuntime.Framework;

/// <summary>
/// The plan-ABI kernel codes the contract's I-DIAG section registers, in its own grouping and spelling. Every
/// rejection this runtime raises about the plan ABI is written from here, so one fact has one code and one
/// spelling: a caller reads the code, never a message. The result-row codes belong to the shared table as well —
/// the row writer is the only caller of those three, and `result-schema` is already raised here at registration.
/// </summary>
public static class RuntimeAbiCodes
{
    // 结果行
    public const string ResultRowBudget = "result-row-budget";
    public const string ResultRowStatus = "result-row-status";
    public const string ResultRowIncomplete = "result-row-incomplete";
    public const string ResultSchema = "result-schema";

    // 句柄与资源
    public const string HandleLiteral = "handle-literal";
    public const string HandleKind = "handle-kind";
    public const string HandleLifetime = "handle-lifetime";
    public const string StaleHandle = "stale-handle";
    public const string HandleBudget = "handle-budget";
    /// <summary>Retired: a resource is now a reference a plan compiles and resolves through its kind's owner
    /// (`stale-resource`) or a value an event payload carries, so no input is refused for being one. The code stays
    /// registered until the contract's own I-DIAG list drops it with the rest of this batch.</summary>
    public const string ResourceRuntimeValue = "resource-runtime-value";
    /// <summary>A plan's data input names an event: an event row is a dispatch identity carried by its own port,
    /// never a literal a plan compiles or a value one step hands to another.</summary>
    public const string EventLiteral = "event-literal";
    public const string StaleResource = "stale-resource";
    public const string ResourceKind = "resource-kind";
    /// <summary>A provider cannot answer for a resource reference: the id names nothing it currently owns. Raised
    /// where the reference is resolved, never where it is carried — a frame moves a resource like any other value.</summary>
    public const string ResourceUnavailable = "resource-unavailable";
    /// <summary>An event value does not match the port's envelope schema, or its row index is outside the
    /// dispatch's own event table.</summary>
    public const string EventPortKind = "event-port-kind";

    // 控制
    public const string ControlShape = "control-shape";
    public const string ControlUnsupported = "control-unsupported";
    public const string ControlBound = "control-bound";
    public const string IterationBudget = "iteration-budget";
    public const string DispatchStepBudget = "dispatch-step-budget";

    // 查询
    public const string QueryBudget = "query-budget";
    public const string QueryObserverUnavailable = "query-observer-unavailable";
    public const string QueryAuthority = "query-authority";
    public const string PureWorldPort = "pure-world-port";
    /// <summary>A `pure` capability declares a read: a declaration is a read even when no port carries one, and
    /// nothing about a pure evaluation may touch the world.</summary>
    public const string PureWorldRead = "pure-world-read";

    // 挂载
    public const string AttachmentEmpty = "attachment-empty";
    public const string AttachmentOrder = "attachment-order";
    public const string AttachmentDuplicate = "attachment-duplicate";
    public const string AttachmentKind = "attachment-kind";
    public const string AttachmentBudget = "attachment-budget";

    // presentation
    /// <summary>A presentation step addresses no player: the recipient set is the step's own routing, and an
    /// empty one is a step that would present to nobody.</summary>
    public const string PresentationRecipient = "presentation-recipient";
    /// <summary>A presentation step's resolved inputs do not fit the payload slot one command request carries.</summary>
    public const string PresentationInputBudget = "presentation-input-budget";
    /// <summary>A presentation handler reported a result that commits state. The tier writes presentation only, so
    /// its result is `none` or it is refused.</summary>
    public const string PresentationCommit = "presentation-commit";
}
