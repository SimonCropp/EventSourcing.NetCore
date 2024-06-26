/// Tracks all Confirmed Carts in the system
/// - Used to walk back through the history of all Carts in the system in approximate order of their processing
/// - Limited to a certain reasonable count of items; snapshot of Items in an epoch needs to stay a sensible size
/// The ConfirmedSeries holds a pointer to the current active epoch for each series
/// Each successive epoch is identified by an index, i.e. ConfirmedEpoch-0_0, then ConfirmedEpoch-0_1
module ECommerce.Domain.ConfirmedEpoch

let [<Literal>] CategoryName = "ConfirmedEpoch"
let streamId epochId = FsCodec.StreamId.gen2 ConfirmedSeriesId.toString ConfirmedEpochId.toString (ConfirmedSeriesId.wellKnownId, epochId)

// NB - these types and the union case names reflect the actual storage formats and hence need to be versioned with care
[<RequireQualifiedAccess>]
module Events =

    type Ingested = { carts : Cart[] }
     and Cart = { cartId : CartId; items : Item[] }
     and Item = { productId : ProductId; unitPrice : decimal; quantity : int }
    type Event =
        | Ingested of Ingested
        | Closed
        interface TypeShape.UnionContract.IUnionContract
    let codec = Store.Codec.gen<Event>
    let codecJsonElement = Store.Codec.genJsonElement<Event>

let ofShoppingCartView cartId (view : ShoppingCart.Details.View) : Events.Cart =
    { cartId = cartId; items = [| for i in view.items -> { productId = i.productId; unitPrice = i.unitPrice; quantity = i.quantity } |] }

let itemId (x : Events.Cart) : CartId = x.cartId
let (|ItemIds|) : Events.Cart[] -> CartId[] = Array.map itemId

module Fold =

    type State = CartId[] * bool
    let initial = [||], false
    let evolve (ids, closed) = function
        | Events.Ingested { carts = ItemIds ingestedIds } -> (Array.append ids ingestedIds, closed)
        | Events.Closed                                   -> (ids, true)

    let fold : State -> Events.Event seq -> State = Seq.fold evolve

let notAlreadyIn (ids : CartId seq) =
    let ids = System.Collections.Generic.HashSet ids
    fun (x : Events.Cart) -> (not << ids.Contains) x.cartId

/// Manages ingestion of only items not already in the list
/// Yields residual net of items already present in this epoch
// NOTE See feedSource template for more advanced version handling splitting large input requests where epoch limit is strict
let decide shouldClose candidates (currentIds, closed as state) : ExactlyOnceIngester.IngestResult<_,_> * Events.Event[] =
    match closed, candidates |> Array.filter (notAlreadyIn currentIds) with
    | false, fresh ->
        let added, events =
            match fresh with
            | [||] -> [||], [||]
            | ItemIds freshIds ->
                let closing = shouldClose currentIds freshIds
                let ingestEvent = Events.Ingested { carts = fresh }
                freshIds, if closing then [| ingestEvent ; Events.Closed |] else [| ingestEvent |]
        let _, closed = Fold.fold state events
        { accepted = added; closed = closed; residual = [||] }, events
    | true, fresh ->
        { accepted = [||]; closed = true; residual = fresh }, [||]

// NOTE see feedSource for example of separating Service logic into Ingestion and Read Services in order to vary the folding and/or state held
type Service internal
    (   shouldClose : CartId[] -> CartId[] -> bool, // let outer layers decide whether ingestion should trigger closing of the batch
        resolve : ConfirmedEpochId -> Equinox.Decider<Events.Event, Fold.State>) =

    /// Ingest the supplied items. Yields relevant elements of the post-state to enable generation of stats
    /// and facilitate deduplication of incoming items in order to avoid null store round-trips where possible
    member _.Ingest(epochId, carts) =
        let decider = resolve epochId
        // NOTE decider which will initially transact against potentially stale cached state, which will trigger a
        // resync if another writer has gotten in before us. This is a conscious decision in this instance; the bulk
        // of writes are presumed to be coming from within this same process
        decider.Transact(decide shouldClose carts, load = Equinox.LoadOption.AnyCachedValue)

    /// Returns all the items currently held in the stream (Not using AllowStale on the assumption this needs to see updates from other apps)
    member _.Read epochId : Async<Fold.State> =
        let decider = resolve epochId
        decider.Query id

module Config =

    let private create_ shouldClose cat = Service(shouldClose, streamId >> Store.createDecider cat)
    let private (|Category|) = function
        | Store.Config.Memory store ->            Store.Memory.create CategoryName Events.codec Fold.initial Fold.fold store
        | Store.Config.Cosmos (context, cache) -> Store.Cosmos.createUnoptimized CategoryName Events.codecJsonElement Fold.initial Fold.fold (context, cache)
        | Store.Config.Dynamo (context, cache) -> Store.Dynamo.createUnoptimized CategoryName Events.codec Fold.initial Fold.fold (context, cache)
        | Store.Config.Esdb (context, cache) ->   Store.Esdb.createUnoptimized CategoryName Events.codec Fold.initial Fold.fold (context, cache)
        | Store.Config.Sss (context, cache) ->    Store.Sss.createUnoptimized CategoryName Events.codec Fold.initial Fold.fold (context, cache)
    let shouldClose maxItemsPerEpoch candidateItems currentItems = Array.length currentItems + Array.length candidateItems >= maxItemsPerEpoch
    let create maxItemsPerEpoch (Category cat) = create_ (shouldClose maxItemsPerEpoch) cat

/// Custom Fold and caching logic compared to the IngesterService
/// - When reading, we want the full Items
/// - Caching only for one minute
/// - There's no value in using the snapshot as it does not have the full state
module Reader =

    type State = Events.Cart[] * bool
    let initial = [||], false
    let evolve (es, closed) = function
        | Events.Ingested e    -> Array.append es e.carts, closed
        | Events.Closed        -> (es, true)
    let fold : State -> Events.Event seq -> State = Seq.fold evolve

    type StateDto = { closed : bool; carts : Events.Cart[] }

    type Service internal (resolve : ConfirmedEpochId -> Equinox.Decider<Events.Event, State>) =

        /// Returns all the items currently held in the stream
        member _.Read(epochId) : Async<StateDto> =
            let decider = resolve epochId
            decider.Query(fun (carts, closed) -> { closed = closed; carts = carts })

    module Config =

        let private (|Category|) = function
            | Store.Config.Memory store ->            Store.Memory.create CategoryName Events.codec initial fold store
            | Store.Config.Cosmos (context, cache) -> Store.Cosmos.createUnoptimized CategoryName Events.codecJsonElement initial fold (context, cache)
            | Store.Config.Dynamo (context, cache) -> Store.Dynamo.createUnoptimized CategoryName Events.codec initial fold (context, cache)
            | Store.Config.Esdb (context, cache) ->   Store.Esdb.createUnoptimized CategoryName Events.codec initial fold (context, cache)
            | Store.Config.Sss (context, cache) ->    Store.Sss.createUnoptimized CategoryName Events.codec initial fold (context, cache)
        let create (Category cat) = Service(streamId >> Store.createDecider cat)
