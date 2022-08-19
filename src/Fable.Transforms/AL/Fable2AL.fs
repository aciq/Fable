﻿module rec Fable.Transforms.Fable2AL

open System
open Fable
open Fable.AST
open Fable.AST.AL
open Fable.AST.Fable
open Fable.Transforms.ALHelpers

type IALCompiler =
    inherit Compiler
    abstract GetEntityName: Fable.Entity -> string
//    abstract PhpNamespace : string
//    abstract MakeUniqueVar: string -> string
//    abstract AddUse : PhpType -> unit
//    abstract AddType : Fable.EntityRef option * PhpType -> unit
//    abstract AddImport : string * bool -> unit
//    abstract AddRequire : PhpType -> unit
//    abstract AddRequire : string -> unit
//    abstract AddLocalVar : Ident -> unit
//    abstract UseVar: Capture -> unit
//    abstract UseVar: string -> unit
//    abstract UseVarByRef: string -> unit
//    abstract SetPhpNamespace: string -> unit
//    abstract AddEntityName: Fable.Entity * string -> unit
//    abstract ClearRequire: string -> unit
//    abstract NewScope: unit -> unit
//    abstract RestoreScope: unit -> Capture list
//    abstract TryFindType: Fable.EntityRef -> Result<PhpType, Fable.Entity>
//    abstract TryFindType: string -> PhpType option
//    abstract IsThisArgument: Fable.Ident -> bool
//    abstract IsImport: string -> bool option
//    abstract DecisionTargets :  (Fable.Ident list * Fable.Expr) list
//    abstract SetDecisionTargets :  (Fable.Ident list * Fable.Expr) list -> unit
//    abstract SetThisArgument : string -> unit
//    abstract ClearThisArgument : unit -> unit
//    abstract Require : (string option * string) list
//    abstract NsUse: PhpType list
//    abstract EnterBreakable: string option -> unit
//    abstract LeaveBreakable: unit -> unit
//    abstract FindLableLevel: string -> int

let fixExt path = Path.ChangeExtension(path, Path.GetExtension(path).Replace("js", "php").Replace("fs", "fs.php"))

let rec convertType (com: IALCompiler)  (t: Type) =
    match t with
    | Type.Number(Int32, _ ) -> "int"
    | Type.String -> "string"
    | DeclaredType(ref,args) ->
        let ent = com.GetEntity(ref)
        com.GetEntityName(ent)
    | Type.List t ->
        convertType com t + "[]"

    | _ -> ""


/// fixes names generated by fable to be php safe
let private fixName (name: string) = name
    
let caseName (com: IALCompiler) (entity: Fable.Entity) (case: UnionCase) =
    if entity.UnionCases.Length = 1 then
        case.Name
    else
        com.GetEntityName entity + "_" + case.Name

/// find the case name from a Tag.
/// Used to instanciate DU cases as classes instances.
let caseNameOfTag ctx (entity: Fable.Entity) tag =
    caseName ctx entity entity.UnionCases.[tag]



/// Return strategy for expression compiled as statements
/// F# is expression based, but some constructs have to be transpiled as
/// statements in other languages. This types indicates how the result
/// should be passed to the resto of the code.
type ReturnStrategy =
      /// The statement should return the value
    | Return
      /// The statement should define a new variable and assign it
    | Let of string
      /// No return value
    | Do
      /// used in decision tree when multiple cases result in the same code
    | Target of string




let nsreplacement (ns: string) =
    match ns.Replace(".",@"\") with
    | "ListModule" -> "FSharpList"
    | "ArrayModule" -> "FSharpArray"
    | "SeqModule" -> "Seq"
    | "SeqModule2" -> "Seq2"
    | ns -> ns

////////
/// START
/// 


let transformBinaryOperationExpr (com: IALCompiler) (ctx:Scope) (returnStrategy: ReturnStrategy) (binaryOperationKind:BinaryOperator, left:Expr, right:Expr) : ALExpression list * Scope =
    match binaryOperationKind with
    | BinaryOperator.BinaryPlus ->
        let left',_ = transform com ctx returnStrategy left
        let right',_ = transform com ctx returnStrategy right
        [ALExpr.binary ALBinaryOperator.Add (left'[0]) (right'[0])], ctx
    | other -> failwith $"unimplemented binary operation: {other}" 

let transformValueExpr (com: IALCompiler) (ctx:Scope) (returnStrategy: ReturnStrategy) (expr: Expr) (valueKind :ValueKind) : ALExpression list * Scope =
    match valueKind with
    | ValueKind.BaseValue(identOption, ``type``) -> raise (NotImplementedException())
    | ValueKind.NumberConstant(value, numberKind, numberInfo) -> [ALExpression.Constant value], ctx
    | other -> failwith $"unimplemented valueKind: {other}"


let transformOperationExpr (com: IALCompiler) (ctx:Scope) (returnStrategy: ReturnStrategy) (operationKind:OperationKind, ``type``:Type, sourceLocationOption:SourceLocation option) : ALExpression list * Scope =
    match operationKind with
    | OperationKind.Binary(binaryOperator, left, right) ->  
        transformBinaryOperationExpr com ctx returnStrategy (binaryOperator, left, right)
    | other -> failwith $"unimplemented operation: {operationKind}"

let transform (com: IALCompiler) (ctx:Scope) (returnStrategy: ReturnStrategy) (expr: Expr): ALExpression list * Scope =
    match expr with
    | Unresolved(_,_,r) ->
        addError com [] r "Unexpected unresolved expression"
        [], ctx
    | Value(valueKind, sourceLocationOption) ->
        transformValueExpr com ctx returnStrategy expr valueKind
    | Fable.Let(ident, value, body) ->
        let scope' = ctx |> Scope.withLocalVar ident
        let valueExpr, scope'' = transform com scope' returnStrategy value
        let letStatement = ALExpr.assignmentIdent ident valueExpr
        let nextStatements, scope''' = transform com scope'' returnStrategy body
        letStatement :: nextStatements, scope'''
    | Fable.Operation(operationKind, ``type``, sourceLocationOption) ->
        transformOperationExpr com ctx returnStrategy (operationKind, ``type``, sourceLocationOption)
    | Fable.IdentExpr ident ->
        [ALExpression.Identifier ident], ctx
    | other -> failwith $"unimplemented expr: {other}"
    
        



type ModuleKind =
    | Default
    | Codeunit  

let getModuleKind (ent:Fable.Entity) =
    if ent |> Entity.hasAttribute Literals.Atts.Codeunit
    then ModuleKind.Codeunit
    else ModuleKind.Default


let transformMemberDecl (com:IALCompiler) (decl:Declaration) =
    match decl with
    | Declaration.MemberDeclaration memberDecl ->
        let mem = com.GetMember(memberDecl.MemberRef)
        let nam = memberDecl.Name
        let retnType = mem.ReturnParameter.Type
        let scope = Scope.create None
        let alExprs, finalScope = transform com scope ReturnStrategy.Return memberDecl.Body
        let alBody = 
            match retnType with
            | Type.Unit -> alExprs
            | _ ->
                // turn last expression into return
                alExprs[..alExprs.Length - 2]
                @ [ALExpression.Exit(alExprs[alExprs.Length - 1])]
                
        { IsLocal = not mem.IsPublic
          ALProcedure.Identifier = memberDecl.Name
          Parameters = []
          LocalVariables = finalScope.localVars
          Statements = alBody
          ReturnType = retnType }
                    
    | _ -> failwith "invalid declaration"
        
    
    

let transformModuleDecl (com:IALCompiler) (moduleDecl:ModuleDecl) =
    let entRef = moduleDecl.Entity
    let ent = com.GetEntity(entRef)
    match ent.Attributes |> Seq.length with
    | 0 -> moduleDecl.Members |> List.collect (transformDecl com) 
    | _ ->
        match getModuleKind ent with
        | Default -> [] 
        | Codeunit ->
            let members = moduleDecl.Members
            let converted = [ for m in members do transformMemberDecl com m ]
            { ObjectId = 50001
              ObjectType = "codeunit" // todo: add type
              Members = converted
              Fields = [] }
            |> ALDecl.ALObjectDecl
            |> List.singleton


let rec transformDecl (com: IALCompiler)  decl : ALDecl list =
    match decl with
    | Declaration.ModuleDeclaration moduleDecl ->
        transformModuleDecl com moduleDecl
    | Declaration.ClassDeclaration classDecl ->
        []
    | Declaration.MemberDeclaration memberDecl ->
        []

    | n ->
        failwith $"Not implemented {n}"

type Capture =
    | ByValue of string
    | ByRef of string

type Scope =
    { 
      mutable localVars: Ident Set
      parent : Scope option
    }
    static member create(parent:Scope option) =
        { 
          localVars = Set.empty
          parent = parent
        }
    static member withLocalVar locv (x:Scope) = {x with localVars = x.localVars |> Set.add locv }


type ALCompiler(com: Fable.Compiler) =
    let mutable types = Map.empty
    let mutable decisionTargets = []
    let mutable scope = Scope.create(None)
    let mutable id = 0
    let mutable isImportValue = Map.empty
    let mutable classNames = Map.empty
    let mutable basePath = ""
    let mutable require = Set.empty
    let mutable nsUse = Set.empty
    let mutable phpNamespace = ""
    let mutable thisArgument = None
    let mutable breakable = []

    member this.AddLocalVar(var, isMutable) =
//        if isMutable then
//            scope.mutableVars <- Set.add var scope.mutableVars
//
//        if scope.capturedVars.Contains(Capture.ByRef var) then
//            ()
//        elif scope.capturedVars.Contains(Capture.ByValue var) then
//            scope.capturedVars <- scope.capturedVars |> Set.remove (Capture.ByValue var)  |> Set.add(ByRef var)
//        else
        scope.localVars <- Set.add var scope.localVars
//
//    member this.UseVar(var) =
//        if not (Set.contains var scope.localVars) && not (Set.contains (ByRef var) scope.capturedVars) then
//            if Set.contains var scope.mutableVars then
//                scope.capturedVars <- Set.add (ByRef var) scope.capturedVars
//            else
//                scope.capturedVars <- Set.add (ByValue var) scope.capturedVars
//
//
//    member this.UseVarByRef(var) =
//        scope.mutableVars <- Set.add var scope.mutableVars
//        if not (Set.contains var scope.localVars) && not (Set.contains (ByRef var) scope.capturedVars) then
//            scope.capturedVars <- Set.add (ByRef var) (Set.remove (ByValue var) scope.capturedVars)
//
//    member this.UseVar(var) =
//        match var with
//        | ByValue name -> this.UseVar name
//        | ByRef name -> this.UseVarByRef name

    member this.MakeUniqueVar(name) =
        id <- id + 1
        "_" + name + "__" + string id

    member this.NewScope() =
        let oldScope = scope
        scope <- Scope.create(Some oldScope)

//    member this.RestoreScope() =
//        match scope.parent with
//        | Some p ->
//            let vars = scope.capturedVars
//            scope <- p
//            for capturedVar in vars do
//                this.UseVar(capturedVar)
//
//            Set.toList vars
//
//        | None -> failwith "Already at top scope"

    member this.AddImport(name, isValue) =
        isImportValue <- Map.add name isValue isImportValue

    member this.AddEntityName(entity: Fable.Entity, name) =
        classNames <- Map.add entity.FullName name classNames

    member this.GetEntityName(e: Fable.Entity) =
        match Map.tryFind e.FullName classNames with
        | Some n -> n
        | None -> e.DisplayName

    member this.AddRequire(file: string) =

        if file.Contains "fable-library" then
            let path = Path.GetFileName (fixExt file)
            require <- Set.add (Some "__FABLE_LIBRARY__",  "/" + path) require

        else
            let fullPhpPath =
                let p = fixExt file
                if Path.IsPathRooted p then
                    p
                else
                    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(com.CurrentFile), p))

            if fullPhpPath <> com.CurrentFile then
                let path =
                    let p = Path.getRelativePath basePath fullPhpPath
                    if p.StartsWith "./" then
                        p.Substring 2
                    else
                        p

                require <- Set.add (Some "__ROOT__" , "/" + path) require

//    member this.AddRequire(typ: PhpType) =
//        this.AddRequire(typ.File)

    member this.ClearRequire(path) =
        basePath <- path
        require <- Set.empty
        nsUse <- Set.empty

//    member this.AddUse(typ: PhpType) =
//        this.AddRequire(typ)
//        nsUse <- Set.add typ nsUse;

    member this.SetPhpNamespace(ns) =
        phpNamespace <- ns

    member this.TryFindType(name: string) =
        Map.tryFind name types

    member this.TryFindType(ref: EntityRef) =
        let ent = com.GetEntity(ref)
        match this.TryFindType(ent.FullName) with
        | Some t -> Ok t
        | None -> Error ent

    member this.IsThisArgument(id: Ident) =
        if id.IsThisArgument then
            true
        else
            let name = fixName id.Name
            if Some name = thisArgument then
                true
            else
                false


    member this.IsImport(name: string) =
        Map.tryFind name isImportValue


    interface IALCompiler with
//        member this.AddType(entref, phpType: PhpType) = this.AddType(entref, phpType)
//        member this.AddLocalVar(var, isMutable) = this.AddLocalVar(var, isMutable)
//        member this.UseVar(var: Capture) = this.UseVar(var)
//        member this.UseVarByRef(var) = this.UseVarByRef(var)
//        member this.UseVar(var: string) = this.UseVar(var)
//        member this.MakeUniqueVar(name) = this.MakeUniqueVar(name)
//        member this.NewScope() = this.NewScope()
//        member this.RestoreScope() = this.RestoreScope()
//        member this.AddImport(name, isValue) = this.AddImport(name, isValue)
//        member this.IsImport(name) = this.IsImport(name)
//        member this.AddEntityName(entity: Fable.Entity, name) = this.AddEntityName(entity, name)
        
//        member this.AddRequire(file: string) = this.AddRequire(file)
//        member this.AddRequire(typ: PhpType) = this.AddRequire(typ)
//        member this.ClearRequire(path) = this.ClearRequire(path)
//        member this.AddUse(typ: PhpType) = this.AddUse(typ)
//        member this.SetPhpNamespace(ns) = this.SetPhpNamespace(ns)
//        member this.TryFindType(entity: Fable.EntityRef) = this.TryFindType(entity)
//        member this.TryFindType(name: string) = this.TryFindType(name)
//        member this.IsThisArgument(id) = this.IsThisArgument(id)
//        member this.DecisionTargets = decisionTargets
//        member this.SetDecisionTargets value = decisionTargets <- value
//        member this.SetThisArgument value = thisArgument <- Some value
//        member this.ClearThisArgument()= thisArgument <- None
//        member this.PhpNamespace = phpNamespace
//        member this.Require = Set.toList require
//        member this.NsUse = Set.toList nsUse

        member this.GetEntityName(e: Fable.Entity) = this.GetEntityName(e)
        member this.IsPrecompilingInlineFunction = com.IsPrecompilingInlineFunction
        member this.WillPrecompileInlineFunction(file) = com.WillPrecompileInlineFunction(file)
        member this.AddLog(msg,severity, rang, fileName, tag) = com.AddLog(msg,severity, ?range = rang, ?fileName= fileName, ?tag = tag)
        member this.AddWatchDependency(file) = com.AddWatchDependency(file)
        member this.GetImplementationFile(fileName) = com.GetImplementationFile(fileName)
        member this.TryGetEntity(fullName) = com.TryGetEntity(fullName)
        member this.GetInlineExpr(fullName) = com.GetInlineExpr(fullName)
        member this.LibraryDir = com.LibraryDir
        member this.CurrentFile = com.CurrentFile
        member this.OutputDir = com.OutputDir
        member this.OutputType = com.OutputType
        member this.ProjectFile = com.ProjectFile
        member this.SourceFiles = com.SourceFiles
        member this.Options = com.Options
        member this.Plugins = com.Plugins
        member this.GetRootModule(fileName) = com.GetRootModule(fileName)
//        member this.EnterBreakable(label) = breakable <- label :: breakable
//        member this.LeaveBreakable() =
//            breakable <- List.tail breakable
//        member this.FindLableLevel(label) =
//            List.findIndex(function Some v when v = label -> true | _ -> false) breakable



module Compiler =

    let transformFile com (file: File) =
        let alComp = ALCompiler(com) :> IALCompiler
        let decls =
            [
                for i,decl in List.indexed file.Declarations do
                    let decls =
                        try
                            transformDecl alComp decl
                        with
                        |    ex ->
                            eprintfn "Error while transpiling decl %d: %O" i ex
                            reraise()
                    for d in decls  do
                        i,d
            ]

        {
            Filename = alComp.CurrentFile + ".al"
            Namespace = Some ""
            Require = []
            Uses = []
            Decls = decls
        }
