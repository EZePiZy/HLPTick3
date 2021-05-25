(*
    Builder.fs

    This module collects functions to build a SimulationGraph, starting from
    a CanvasState. It also runs all the checks contained in Analyser to validate
    the graph is correct (and can be built in the first place).
*)

module SimulationBuilder

open Helpers
open NumberHelpers
open CommonTypes
open SimulatorTypes
open CanvasStateAnalyser

/// Assert that the FData only contain a single bit, and return such bit.
let inline extractBit (fd: FData) : uint32 =
#if DEBUG
    assertThat (fd.Width = 1)
    <| sprintf "extractBit called with wireData: %A" fd
#endif
    match fd.Dat with | Word n -> n | BigWord _ -> failwithf "Can't extract 1 bit from BigWord data {wireData}"

let inline packBit (bit: uint32) : FData = if bit = 0u then {Dat=Word 0u; Width = 1} else {Dat = Word 1u; Width = 1}


/// This function should only be called on Component ports, never on Connection
/// ports: ports in Components should always have Some portNumber, ports in
/// Connections should have None.
let private getPortNumberOrFail port =
    match port with
    | None -> failwithf "what? Component ports should always have a portNumber"
    | Some p -> p

let private bitNot bit =
    match bit with
    | Zero -> One
    | One -> Zero

let private bitAnd bit0 bit1 =
    match bit0, bit1 with
    | One, One -> One
    | _, _ -> Zero

let private bitOr bit0 bit1 =
    match bit0, bit1 with
    | Zero, Zero -> Zero
    | _, _ -> One

let private bitXor bit0 bit1 =
    match bit0, bit1 with
    | Zero, One | One, Zero -> One
    | _, _ -> Zero

let private bitNand bit0 bit1 =
    bitAnd bit0 bit1 |> bitNot

let private bitNor bit0 bit1 =
    bitOr bit0 bit1 |> bitNot

let private bitXnor bit0 bit1 =
    bitXor bit0 bit1 |> bitNot

/// Make sure that the size of the inputs of a SimulationComponent is as
/// expected.
let private assertNotTooManyInputs
        (reducerInput : ReducerInput)
        (cType : ComponentType)
        (expected : int)
        : unit =
#if ASSERTS
    assertThat (reducerInput.Inputs.Count <= expected)
    <| sprintf "assertNotTooManyInputs failed for %A: %d > %d" cType reducerInput.Inputs.Count expected  
#else
    ()
#endif

/// Make sure combinational logic does not handle clock ticks.
let private assertNoClockTick
            (reducerInput : ReducerInput)
            (cType : ComponentType)
            : unit =
#if ASSERTS
    assertThat (reducerInput.IsClockTick = No)
    <| sprintf "Unexpected IsClockTick = Yes in combinational logic reducer input for %A" cType
#else
    ()
#endif


let private assertValidBus (bus : WireData) (minWidth : int) compType : unit =
#if ASSERTS
    assertThat (bus.Length >= minWidth)
    <| sprintf "%A bus has invalid width: %d < %d" compType bus.Length minWidth
#else
    ()
#endif

/// Extract the values of the inputs of a SimulationComponent.
/// If any of these inputs is missing, return None.
/// The values are returned in the the passed order. E.g. if portNumbers is
/// [0, 1, 2], the returned value will be [bit0, bit1, bit2].
let rec private getValuesForPorts
        (inputs : Map<InputPortNumber, WireData>)
        (portNumbers : InputPortNumber list)
        : (WireData list) option =
    match portNumbers with
    | [] -> Some []
    | portNumber :: portNumbers' ->
        match inputs.TryFind portNumber with
        | None -> None
        | Some wireData ->
            match getValuesForPorts inputs portNumbers' with
            | None -> None
            | Some values -> Some <| wireData :: values

/// Assert that the wireData only contain a single bit, and return such bit.
let private extractBit (wireData : WireData) : Bit =
#if ASSERTS
    assertThat (wireData.Length = 1) <| sprintf "extractBit called with wireData: %A" wireData
#endif
    wireData.[0]

let private packBit (bit : Bit) : WireData = [bit]

/// Read the content of the memory at the specified address.
let private readMemory (mem : Memory1) (address : WireData) : WireData =
    let intAddr = convertWireDataToInt address
    let outDataInt = Helpers.getMemData intAddr mem
    convertIntToWireData mem.WordWidth outDataInt

/// Write the content of the memory at the specified address.
let private writeMemory (mem : Memory1) (address : WireData) (data : WireData) : Memory1 =
    let intAddr = convertWireDataToInt address
    let intData = convertWireDataToInt data
    {mem with Data = Map.add intAddr intData mem.Data}

/// Reducer outputs for when a component has not enough inputs to produce an
/// actual output.
let private notReadyReducerOutput state = {
    Outputs = None
    NewCustomSimulationGraph = None
    NewState = state
}

/// Make reducer outputs for NOT-custom components.
let private makeReducerOutput state outputs = {
    Outputs = Some outputs
    NewCustomSimulationGraph = None
    NewState = state
}

let private getDffStateBit state =
    match state with
    | DffState bit -> bit
    | _ -> failwithf "what? getDffStateBit called with an invalid state: %A" state

let private getRegisterStateBits state =
    match state with
    | RegisterState bits -> bits
    | _ -> failwithf "what? getRegisterStateBits called with an invalid state: %A" state

let private getRamStateMemory state =
    match state with
    | RamState memory -> memory
    | _ -> failwithf "what? getRamStateMemory called with an invalid state: %A" state

/// Given a component type, return a function takes a ReducerInput and
/// transform it into a ReducerOuptut.
/// The ReducerOutput should have Outputs set to None if there are not enough
/// ReducerInput.Inputs to calculate the outputs.
/// For custom components, return a fake version of the reducer, that has to be
/// replaced when resolving the dependencies.
/// TODO: some components reducers are quite similar, for example Register and
/// RegisterE and DFF and DFFE. It is probably a good idea to merge them
/// together to avoid duplicated logic.
let private getReducer (componentType : ComponentType) : ReducerInput -> ReducerOutput =
    fun _ -> failwithf "Reducer function is legacy code and should never be called!"



/// Build a map that, for each source port in the connections, keeps track of
/// the ports it targets.
/// It makes no sense to extract the PortNumber in this function as it is always
/// set to None for ports in connections.
let private buildSourceToTargetPortMap
        (connections : Connection list)
        : Map<OutputPortId, (ComponentId * InputPortId) list> =
    (Map.empty, connections) ||> List.fold (fun map conn ->
        let key = OutputPortId conn.Source.Id
        let target = ComponentId conn.Target.HostId, InputPortId conn.Target.Id
        // Append the new target to the list associated with the key.
        let newValue =
            match map.TryFind key with
            | None -> [target]
            | Some oldValue -> target :: oldValue
        map.Add (key, newValue)
    )

/// For each input port in each component, map it to its port number.
let private mapInputPortIdToPortNumber
        (components : Component list)
        : Map<InputPortId, InputPortNumber> =
    (Map.empty, components) ||> List.fold (fun map comp ->
        (map, comp.InputPorts) ||> List.fold (fun map port ->
            map.Add (InputPortId port.Id,
                     InputPortNumber (getPortNumberOrFail port.PortNumber))
        )
    )

/// Get the default state for a component.
/// Note that custom components are stateless, even though they may contain
/// stateful components. The state of such stateful components is maintained
/// in the CustomSimulationGraph.
/// ROMs are stateless (they are only defined by their initial content).
let private getDefaultState compType =
    match compType with
    | ROM _ | RAM _ | AsyncROM _ -> 
        failwithf "What? Legacy RAM component types should never occur"
    | Input _ | Output _ | IOLabel | BusSelection _ | BusCompare _ | Not | And | Or | Xor | Nand | Nor | Xnor | Mux2 | Decode4
    | Demux2 | NbitsAdder _ |NbitsXor _ | Custom _ | MergeWires | SplitWire _ | ROM1 _  | Viewer _ -> NoState
    | Constant1 _ | Constant _ -> NoState 
    | AsyncROM1 _ -> NoState
    | DFF | DFFE -> DffState 0u
    | Register w | RegisterE w -> RegisterState <| convertIntToFastData w 0u
    | RAM1 memory -> RamState memory // The RamState content may change during
                                    // the simulation.

/// Build a simulation component.
let private buildSimulationComponent
        (sourceToTargetPort : Map<OutputPortId, (ComponentId * InputPortId) list>)
        (portIdToPortNumber : Map<InputPortId, InputPortNumber>)
        (comp : Component)
        : SimulationComponent =
    // Remove portIds and use portNumbers instead.
    let mapPortIdsToPortNumbers
            (targets : (ComponentId * InputPortId) list)
            : (ComponentId * InputPortNumber) list =
        targets |> List.map (fun (compId, portId) ->
            match portIdToPortNumber.TryFind <| portId with
            | None -> failwithf "what? Input port with portId %A has no portNumber associated" portId
            | Some portNumber -> compId, portNumber
        )
    // For each output port, find out which other components and ports are
    // connected to it.
    let outputs =
        comp.OutputPorts
        |> List.collect (fun port ->
            match sourceToTargetPort.TryFind <| OutputPortId port.Id with
            | None when comp.Type=IOLabel -> [] // IOLabels are allowed to be connected to nothing
            | None -> failwithf "what? Unconnected output port %s in comp %s" port.Id comp.Id
            | Some targets -> [
                                OutputPortNumber <| getPortNumberOrFail port.PortNumber,
                                mapPortIdsToPortNumbers targets
                              ]
        )
        |> Map.ofList



    // The inputs will be set during the simulation, we just need to initialise
    // the ones for Output nodes, see below.
    let inputs =
        match comp.Type with
        | Output width ->
            // Initialise all outputs to zero. This is necessary because upon
            // starting the simulation we need to feed all zeros. To do so, we
            // need to feed all simulation inputs set to zero and a global clock
            // tick. The problem is that both operations expect all outputs to
            // be set as result, but they don't necessarily set all the outputs
            // themselves. Therefore there is not an order you can run them in
            // that will always work. Presetting the outputs solves the problem
            // and the value does not matter as all outputs will be set again
            // in that initialization process.
            Map.empty.Add (InputPortNumber 0, List.replicate width Zero)
        | _ -> Map.empty
    {
        Id = ComponentId comp.Id
        Type = comp.Type
        Label = ComponentLabel comp.Label
        Inputs = inputs
        Outputs = outputs
        OutputsPropagated = Array.replicate 0 false // default for non-clocked components
        CustomSimulationGraph = None // Custom components will be augumented by the DependencyMerger.
        State = getDefaultState comp.Type
        Reducer = getReducer comp.Type
    }

let getLabelConnections (comps:Component list) (conns: Connection list) =
    let labels = 
        comps 
        |> List.filter (fun co -> co.Type = IOLabel)

    let compIdMap =
        labels
        |> List.map (fun co -> ComponentId co.Id, co)
        |> Map.ofList

    let getComp n = compIdMap.[n]

    let targetMap =
        conns
        |> List.map (fun conn -> ComponentId conn.Target.HostId, conn)
        |> Map.ofList

    let getConnection (compTarget:Component) = targetMap.[ComponentId compTarget.Id]

    let copyConnection (conn: Connection) (compTarget:Component) (tagNum:int) =
        {conn with Target = compTarget.InputPorts.[0]; Id = sprintf "iolab%d" tagNum + conn.Id}

    let getDriverConnection (comps: Component list) =
        comps
        |> List.tryFind (fun co -> (Map.tryFind (ComponentId co.Id) targetMap) <> None)
        |> function 
            | None -> failwithf "What? component cannot be found in %A" targetMap
            | Some comp -> targetMap.[ComponentId comp.Id]

    labels
    |> List.groupBy (fun co -> co.Label)
    |> List.collect (fun (lab,lst) -> 
        let dConn = getDriverConnection lst
        lst
        |> List.filter (fun co -> co.Id <> dConn.Target.HostId)
        |> List.indexed
        |> List.map (fun (i, co) -> copyConnection dConn co i))



/// Transforms a canvas state into a simulation graph.
let private buildSimulationGraph (canvasState : CanvasState) : (SimulationGraph) =
    let components, connections' = canvasState
    let labConns = getLabelConnections components connections'
    let connections = labConns @ connections'
    let sourceToTargetPort = buildSourceToTargetPortMap connections
    let portIdToPortNumber = mapInputPortIdToPortNumber components
    let mapper = buildSimulationComponent sourceToTargetPort portIdToPortNumber
    components
    |> List.map (fun comp -> ComponentId comp.Id, mapper comp)
    |> Map.ofList
    |> (fun m -> m)

/// Validate a diagram and generate its simulation graph.
let runCanvasStateChecksAndBuildGraph
        (canvasState : CanvasState)
        (loadedComponents: LoadedComponent list)
        : Result<SimulationGraph, SimulationError> =
    match analyseState canvasState loadedComponents with
    | Some err -> Error err
    | None -> Ok <| buildSimulationGraph canvasState
