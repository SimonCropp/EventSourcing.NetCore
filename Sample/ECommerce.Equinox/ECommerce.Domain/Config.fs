module ECommerce.Domain.Config

let log = Serilog.Log.ForContext("isMetric", true)
let createDecider category = Equinox.Decider.resolve log category

module Memory =

    let create codec initial fold store : Equinox.Category<_, _, _> =
        Equinox.MemoryStore.MemoryStoreCategory(store, codec, fold, initial)

module EventCodec =

    open FsCodec.SystemTextJson

    let private defaultOptions = Options.Create(autoTypeSafeEnumToJsonString = true)
    let genJsonElement<'t when 't :> TypeShape.UnionContract.IUnionContract> =
        CodecJsonElement.Create<'t>(options = defaultOptions)
    let gen<'t when 't :> TypeShape.UnionContract.IUnionContract> =
        Codec.Create<'t>(options = defaultOptions)

let private defaultCacheDuration = System.TimeSpan.FromMinutes 20.

module Cosmos =

    let private createCached codec initial fold accessStrategy (context, cache) =
        let cacheStrategy = Equinox.CosmosStore.CachingStrategy.SlidingWindow (cache, defaultCacheDuration)
        Equinox.CosmosStore.CosmosStoreCategory(context, codec, fold, initial, cacheStrategy, accessStrategy)

    let createUnoptimized codec initial fold (context, cache) =
        let accessStrategy = Equinox.CosmosStore.AccessStrategy.Unoptimized
        createCached codec initial fold accessStrategy (context, cache)

    let createSnapshotted codec initial fold (isOrigin, toSnapshot) (context, cache) =
        let accessStrategy = Equinox.CosmosStore.AccessStrategy.Snapshot (isOrigin, toSnapshot)
        createCached codec initial fold accessStrategy (context, cache)

    let createRollingState codec initial fold toSnapshot (context, cache) =
        let accessStrategy = Equinox.CosmosStore.AccessStrategy.RollingState toSnapshot
        createCached codec initial fold accessStrategy (context, cache)

module Dynamo =

    let private createCached codec initial fold accessStrategy (context, cache) =
        let cacheStrategy = Equinox.DynamoStore.CachingStrategy.SlidingWindow (cache, defaultCacheDuration)
        Equinox.DynamoStore.DynamoStoreCategory(context, codec |> FsCodec.Deflate.EncodeUncompressed, fold, initial, cacheStrategy, accessStrategy)

    let createUnoptimized codec initial fold (context, cache) =
        let accessStrategy = Equinox.DynamoStore.AccessStrategy.Unoptimized
        createCached codec initial fold accessStrategy (context, cache)

    let createSnapshotted codec initial fold (isOrigin, toSnapshot) (context, cache) =
        let accessStrategy = Equinox.DynamoStore.AccessStrategy.Snapshot (isOrigin, toSnapshot)
        createCached codec initial fold accessStrategy (context, cache)

    let createRollingState codec initial fold toSnapshot (context, cache) =
        let accessStrategy = Equinox.DynamoStore.AccessStrategy.RollingState toSnapshot
        createCached codec initial fold accessStrategy (context, cache)

module Esdb =

    let private createCached codec initial fold accessStrategy (context, cache) =
        let cacheStrategy = Equinox.EventStoreDb.CachingStrategy.SlidingWindow (cache, defaultCacheDuration)
        Equinox.EventStoreDb.EventStoreCategory(context, codec, fold, initial, cacheStrategy, ?access = accessStrategy)
    let createUnoptimized codec initial fold (context, cache) =
        createCached codec initial fold None (context, cache)
    let createLatestKnownEvent codec initial fold (context, cache) =
        createCached codec initial fold (Some Equinox.EventStoreDb.AccessStrategy.LatestKnownEvent) (context, cache)

module Sss =

    let private createCached codec initial fold accessStrategy (context, cache) =
        let cacheStrategy = Equinox.SqlStreamStore.CachingStrategy.SlidingWindow (cache, defaultCacheDuration)
        Equinox.SqlStreamStore.SqlStreamStoreCategory(context, codec, fold, initial, cacheStrategy, ?access = accessStrategy)
    let createUnoptimized codec initial fold (context, cache) =
        createCached codec initial fold None (context, cache)
    let createLatestKnownEvent codec initial fold (context, cache) =
        createCached codec initial fold (Some Equinox.SqlStreamStore.AccessStrategy.LatestKnownEvent) (context, cache)

[<NoComparison; NoEquality; RequireQualifiedAccess>]
type Store<'t> =
    | Memory of Equinox.MemoryStore.VolatileStore<'t>
    | Cosmos of Equinox.CosmosStore.CosmosStoreContext * Equinox.Core.ICache
    | Dynamo of Equinox.DynamoStore.DynamoStoreContext * Equinox.Core.ICache
    | Esdb of Equinox.EventStoreDb.EventStoreContext * Equinox.Core.ICache
    | Sss of Equinox.SqlStreamStore.SqlStreamStoreContext * Equinox.Core.ICache
