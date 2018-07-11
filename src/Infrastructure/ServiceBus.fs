module Infrastructure.ServiceBus

open FsNetMQ
open MBrace.FsPickler

module Message = ServiceBusMessage

let binarySerializer = FsPickler.CreateBinarySerializer()

let private getBrokerAddress name =
    sprintf "inproc://servicebus-%s" name

let private waitForAck socket =
    let ack = Message.recv socket

    match ack with
    | None -> failwith "malformed ack"
    | Some ack ->
        match ack with
        | Message.Ack _ -> ()
        | x -> failwithf "expectingAck, got %A" x

module Broker =
    type private Service = {
        routingId: byte[] option;
        pendingMessages: Message.T list;
    }

    type Broker = {
        observer: System.IDisposable;
        server: Socket.T;
        poller: Poller.T;
    }

    type T =
        | Broker of Broker
        interface System.IDisposable with
            member x.Dispose () =
                match x with
                | Broker b ->
                    b.observer.Dispose ()
                    Poller.removeSocket b.poller b.server
                    (b.server :> System.IDisposable).Dispose ()

    let create poller name =
        let sendAck server routingId =
            Frame.sendMore server routingId
            Message.send server ServiceBusMessage.Ack

        let sendToService server services serviceName msg =
            match Map.tryFind serviceName services with
            | Some service ->
                match service.routingId with
                | Some routingId ->
                    Frame.sendMore server routingId
                    Message.send server msg
                    services
                | None ->
                    let service =
                        {service with pendingMessages = msg :: service.pendingMessages}
                    Map.add serviceName service services
            | None ->
                let service = {routingId = None; pendingMessages= [msg]}
                Map.add serviceName service services

        let processMessage server services routingId = function
            | Message.Register service ->
                sendAck server routingId

                match Map.tryFind service services with
                | None -> ()
                | Some service ->
                    service.pendingMessages
                    |> List.iter (fun msg ->
                        Frame.sendMore server routingId
                        Message.send server msg)

                Map.add service {routingId=Some routingId; pendingMessages=[]} services

            | Message.Command c ->
                sendAck server routingId

                sendToService server services c.service (Message.RelayCommand c.payload)

            | Message.Request r ->
                sendToService server services r.service (Message.RelayRequest {sender=routingId;payload=r.payload})

            | Message.Response r ->
                Frame.sendMore server r.sender
                Message.send server (Message.RelayResponse r.payload)
                services

            | _ -> services

        let server = Socket.router ()
        Options.setRecvHighwatermark server 0 |> ignore
        Options.setSendHighWatermark server 0 |> ignore
        Socket.bind server (getBrokerAddress name)


        let observer =
            Poller.addSocket poller server
            |> Observable.scan (fun services _ ->
                let routingId,_ = Frame.recv server
                let message = Message.recv server
                match message with
                | None -> services
                | Some message -> processMessage server services routingId message
                ) Map.empty
            |> Observable.subscribe (ignore)

        Broker {observer=observer; server=server;poller=poller;}

module Agent =

    type RequestId(socket:FsNetMQ.Socket.T, sender:byte[]) =
        member this.reply<'a>(msg:'a) =
            Message.send socket (Message.Response {
               sender=sender;
               payload=binarySerializer.Pickle msg;
            })

    type Message<'command,'request> =
        | Command of 'command
        | Request of RequestId * 'request

    type Agent<'command,'request> = {
        observable: System.IObservable<Message<'command, 'request>>;
        observer: System.IDisposable;
        socket: Socket.T;
        poller: Poller.T;
    }

    type T<'command,'request> =
        | Agent of Agent<'command,'request>
        interface System.IDisposable with
            member x.Dispose () =
                match x with
                | Agent a ->
                    Poller.removeSocket a.poller a.socket
                    a.observer.Dispose ()
                    (a.socket :> System.IDisposable).Dispose ()

    let create<'command,'request> poller name service =
        let socket = Socket.dealer ()
        Options.setRecvHighwatermark socket 0 |> ignore
        Options.setSendHighWatermark socket 0 |> ignore
        Socket.connect socket (getBrokerAddress name)

        Message.send socket (Message.Register service)
        waitForAck socket

        let observable =
            Poller.addSocket poller socket
            |> Observable.choose (fun _ -> Message.recv socket)
            |> Observable.choose (fun msg ->
                match msg with
                | Message.RelayCommand c ->
                    Some (Command (binarySerializer.UnPickle<'command> c))
                | Message.RelayRequest r ->
                    let payload = binarySerializer.UnPickle<'request> r.payload
                    Some (Request (new RequestId(socket, r.sender), payload))
                | _ -> None)
            |> FSharp.Control.Reactive.Observable.publish

        let observer = FSharp.Control.Reactive.Observable.connect observable


        Agent {observable=observable; socket=socket;poller=poller; observer=observer}

    let observable (Agent agent) = agent.observable

module Client =
    type T = Socket.T

    let create name =
        let client = Socket.dealer ()
        Options.setRecvHighwatermark client 0 |> ignore
        Options.setSendHighWatermark client 0 |> ignore

        Socket.connect client (getBrokerAddress name)

        client

    module Command =
        let send client service command =
            let payload = binarySerializer.Pickle command

            Message.send client (Message.Command {service=service; payload = payload})

            waitForAck client

    module Request =
        let send<'request,'response> client service (request:'request) =
            let payload = binarySerializer.Pickle request

            Message.send client (Message.Request {service=service; payload = payload})

            let response = Message.recv client

            match response with
            | None -> failwith "malformed response"
            | Some r ->
                match r with
                | Message.RelayResponse r -> binarySerializer.UnPickle<'response> r
                | x -> failwithf "expecting RelayResponse, got %A" x

        let trySend<'request,'response> client service (request:'request) =
            let payload = binarySerializer.Pickle request

            Message.send client (Message.Request {service=service; payload = payload})

            let response = Message.tryRecv client 10000<milliseconds>

            match response with
            | None -> None
            | Some r ->
                match r with
                | Message.RelayResponse r -> binarySerializer.UnPickle<'response> r |> Some
                | x -> failwithf "expecting RelayResponse, got %A" x
