namespace OctreeLod.Core.Model;

// All node metadata access is routed through this interface (never a
// concrete array/list reached into directly from engine code) so a future
// out-of-core metadata store — not needed at today's scale, see design notes
// — is a swap-in rather than a rewrite.
public interface INodeMetadataStore
{
    long RootId { get; set; }
    int Count { get; }

    long Allocate(NodeRecord record);
    NodeRecord Get(long id);
    void Set(long id, NodeRecord record);
}
