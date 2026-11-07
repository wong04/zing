namespace Zing.Server;

public static class ManifestEndpoints
{
	public static void MapManifest(this IEndpointRouteBuilder app)
	{
		app.MapGet("/manifest", (HttpContext context, Db db) => VersionedBlobs.Get(db, SyncEndpoints.UserId(context), BlobSlot.Manifest));
		app.MapPut("/manifest", async (HttpContext context, Db db) =>
			VersionedBlobs.Put(db, SyncEndpoints.UserId(context), BlobSlot.Manifest, await SyncEndpoints.ReadBody<BlobPut>(context)));
	}
}
