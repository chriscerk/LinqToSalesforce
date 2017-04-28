﻿namespace LinqToSalesforce

open System
open System.Reflection
open Rest
open Rest.OAuth
open System.Collections
open System.Collections.Generic
open System.Linq
open Entities
open Translator
open Visitor

module private ContextHelper =
  let isCountQuery = List.exists (function | Count -> true | _ -> false)
  
  let executeCount(client:Client) operations tableName fieldsProviders =
    let soql = buildSoql operations tableName fieldsProviders
    let rs = client.ExecuteSoql<int32> soql |> Async.RunSynchronously
    match rs with
    | Success r -> r.TotalSize
    | Failure [e] -> e.ToException() |> raise
    | Failure errors -> 
        errors 
          |> List.map (fun e -> e.ToException() :> Exception) 
          |> List.toArray
          |> AggregateException
          |> raise

  let execute<'t when 't :> ISalesforceEntity>(client:Client) (tracker:Tracker) operations tableName fieldsProviders =
    //let typ = typeof<'t>
    let soql = buildSoql operations tableName fieldsProviders
    //printfn "SOQL: %s" soql
    let rs = client.ExecuteSoql<'t> soql |> Async.RunSynchronously
    match rs with
    | Success r ->
      let records = r.Records.ToList()
      for r in records do
        r.TrackPropertyUpdates()
        r.PropertyChanged.Add(fun _ -> tracker.Track r)
      records
    | Failure [e] -> e.ToException() |> raise
    | Failure errors -> 
        errors 
          |> List.map (fun e -> e.ToException() :> Exception) 
          |> List.toArray
          |> AggregateException
          |> raise

type RelationShip<'tp,'tc
  when 'tp :> ISalesforceEntity 
  and 'tc :> ISalesforceEntity>(client:Client, referenceField:string, tracker:Tracker, parent:'tp, fieldsProviders) =

  let loadResults () =
    let childType = typeof<'tc>
    let chidlTableName = findEntityName childType
    let field = { Name=referenceField; DecorationName=None; Type=typeof<string> }
    let cmp = { Field=field; Kind=ComparisonKind.Equal; Target=(Constant(parent.Id)) }
    let operations = 
      [ Select (SelectType childType)
        Where (UnaryComparison(cmp)) ]
    let fp = FieldsProviders.fieldsFromTypeProvider<'tc>
    let results = ContextHelper.execute<'tc> client tracker operations chidlTableName fp
    for r in results do
      RelationShip<'tp,_>.Build childType client tracker r
    results
  let results = lazy (loadResults ())
  member __.Results () =
    results.Value
  interface IEnumerable<'tc> with
    member x.GetEnumerator(): IEnumerator = 
      results.Value.GetEnumerator() :> IEnumerator
    member x.GetEnumerator(): IEnumerator<'tc> = 
      results.Value.GetEnumerator() :> IEnumerator<'tc>

  static member Build (typ:Type) (client:Client) (tracker:Tracker) (parent:#ISalesforceEntity) fieldsProviders =
    let td = typedefof<RelationShip<_,_>>
    typ.GetProperties() 
      |> Seq.filter (
          fun p -> 
            p.PropertyType.IsGenericType && (p.PropertyType.GetGenericTypeDefinition() = td)
          )
      |> Seq.iter (
          fun r1 ->
            //let ct = parent.GetType()
            //let co = r1.PropertyType.GetConstructor([|typeof<Client>; typeof<string>; typeof<Tracker>; ct|])
            let co = r1.PropertyType.GetConstructors() |> Seq.head
            let referencedByFieldAttr = r1.GetCustomAttributes<ReferencedByFieldAttribute>() |> Seq.head
            let instance = co.Invoke([|client; referencedByFieldAttr.Name; tracker; parent; fieldsProviders|])
            r1.SetValue(parent, instance)
         )

type SoqlQueryContext<'t when 't :> ISalesforceEntity>(client:Client, tracker:Tracker, fieldsProviders, ?pTableName:string) =
  interface IQueryContext with
    member x.Execute expression _ =
      let visitor = new RequestExpressionVisitor(expression)
      visitor.Visit()
      let operations = visitor.Operations |> Seq.toList
      let typ = typeof<'t>
      let tableName = match pTableName with | Some n -> n | None -> findEntityName typ
      if ContextHelper.isCountQuery operations
      then
        let count = ContextHelper.executeCount client operations tableName fieldsProviders
        count :> obj
      else
        let results = ContextHelper.execute<'t> client tracker operations tableName fieldsProviders
        for r in results do
          RelationShip<'t,_>.Build typ client tracker r fieldsProviders
        results :> obj

type SoqlContext (instanceName:string, authparams:ImpersonationParam) =
  let client = Client(instanceName, authparams)
  let tracker = new Tracker()
  
  member x.GetIdentity() =
    client.GetIdentity()

  member x.GetTable<'t when 't :> ISalesforceEntity>() =
    let c = new SoqlQueryContext<'t>(client, tracker, FieldsProviders.fieldsFromTypeProvider<'t>)
    let tableName = typeof<'t>.Name
    let queryProvider = new QueryProvider(c, tableName)
    new Queryable<'t>(queryProvider, tableName)
  
  member x.BuildQueryable<'t when 't :> ISalesforceEntity>(table:TableDesc) =
    let fieldsProviders () =
      table.Fields
      |> Seq.map (fun f -> f.Name)
      |> Seq.toArray
    let c = new SoqlQueryContext<'t>(client, tracker, fieldsProviders, table.Name)
    let queryProvider = new QueryProvider(c, table.Name)
    new Queryable<'t>(queryProvider, table.Name)

  member x.Insert entity =
    match client.Insert entity with
    | Success r ->
        entity.PropertyChanged.Add
          <| fun _ -> tracker.Track entity
        entity.Id <- r.Id
        r.Id
    | Failure [e] -> e.ToException() |> raise
    | Failure errors -> 
        errors 
          |> List.map (fun e -> e.ToException() :> Exception) 
          |> List.toArray
          |> AggregateException
          |> raise
  
  //Note: writing 'member x.Delete = client.Delete' is causing C# interop difficulties because it wants FSharp.Core ref
  member x.Delete entity =
    client.Delete entity

  member x.Save() : unit =
    let entities = tracker.GetTrackedEntities()
    let errors =
        entities
        |> List.choose (
            fun e ->
              match client.Update e with
              | Success _ -> None
              | Failure errors ->
                  errors
                  |> Array.map (fun error -> error.ToException() :> Exception)
                  |> Some)
        |> List.toArray
        |> Array.collect (fun e -> e)
    tracker.Clear()
    if errors.Length > 0
    then raise (new AggregateException("Cannot save all entities", errors))

