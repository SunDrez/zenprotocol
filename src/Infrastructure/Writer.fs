module Infrastructure.Writer

open Infrastructure.EventBus

// TODO: currently we only need events, however we will also need commands and DB changes in the future
type Writer<'effect,'a> = Writer of 'effect list * 'a

let unwrap (Writer (events,x)) = events,x

let bind (Writer (events,x)) f =         
    let events', x' = unwrap (f x)        
    let events'' = List.append events events'
    
    Writer (events'', x')                 
    
let ret x = 
    Writer ([],x)         
    
type WriterBuilder<'effect>() = 
    member this.Bind<'a,'b> ((mx:Writer<'effect,'a>), (f:'a-> Writer<'effect, 'b>)) = 
        bind mx f
    member this.Return<'a> (x) : Writer<'effect, 'a> = ret x   