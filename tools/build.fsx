﻿#I "lib"
#load "lib/literate.fsx"
#load "lib/HttpServer.fs"
#r "System.Xml.Linq.dll"
#r "lib/System.Web.Razor.dll"
#r "lib/RazorEngine.dll"
#r "tilde/bin/Debug/TildeLib.dll"
open System
open System.IO
open FSharp.Literate
open FSharp.Http

// --------------------------------------------------------------------------------------
// Various file & directory helpers
// --------------------------------------------------------------------------------------
module FileHelpers =

  /// Concantenate path using the right separator
  let (++) p1 p2 = Path.Combine(p1, p2)

  /// Delete directory if it exists
  let SafeDeleteDir (directory:string) recurse =     
    if Directory.Exists(directory) then 
      Directory.Delete(directory, recurse)
    

  /// Ensure that a given directory exists
  let rec EnsureDirectory directory = 
    if Directory.Exists(directory) |> not then 
      EnsureDirectory (Path.GetDirectoryName(directory))
      Directory.CreateDirectory(directory) |> ignore

  /// Copy files recursively and ensure all directories are created
  /// (overwrites older files)
  let rec CopyFiles source target = 
    EnsureDirectory target
    for dir in Directory.GetDirectories(source) do
      CopyFiles dir (target ++ Path.GetFileName(dir))
    for file in Directory.GetFiles(source) do
      let fullTarget = target ++ Path.GetFileName(file)
      if not (File.Exists(fullTarget)) || 
         File.GetLastWriteTime(file) > File.GetLastWriteTime(fullTarget) then
        printfn "Copying: %s" file
        File.Copy(file, fullTarget, true)

  /// Returns a file name in the TEMP folder and deletes it when disposed
  type DisposableFile(file, deletes) =
    static member Create(file) =
      new DisposableFile(file, [file])
    static member CreateTemp(?extension) = 
      let temp = Path.GetTempFileName()
      let file = match extension with Some ext -> temp + ext | _ -> temp
      new DisposableFile(file, [temp; file])
    member x.FileName = file
    interface System.IDisposable with
      member x.Dispose() = 
        for delete in deletes do
          if File.Exists(delete) then File.Delete(delete)


  /// Get all *.cshtml, *.html, *.md and *.fsx files in the source directory
  /// and return tuple containing the source file and required target file
  let GetSourceFiles source output = seq {
    let exts = set [ ".md"; ".fsx"; ".cshtml"; ".html" ]
    let rec getFiles source = seq {
      yield! Directory.GetFiles(source)
      for dir in Directory.GetDirectories(source) do
        yield! getFiles dir }
    for file in getFiles source do
      if exts |> Set.contains (Path.GetExtension(file).ToLower()) then
        let relativeFile = file.Remove(0, source.Length + 1)
        let relativeFolder = 
          let idx = relativeFile.LastIndexOf('.') 
          relativeFile.Substring(0, idx)
        let output = output ++ relativeFolder
        yield file, output }


  /// Given a sequence of source - output files, return only those where the
  /// source has changed since the output was generated. If any of the dependencies
  /// is newer than the output, then a file is also returned.
  let FilterChangedFiles dependencies special files = seq {
    let newestDependency = dependencies |> List.map Directory.GetLastWriteTime |> List.max
    let special = set special
    for source, output in files do 
      let outputWrite = Directory.GetLastWriteTime(output)
      if Set.contains source special || 
         outputWrite < Directory.GetLastWriteTime(source) || 
         outputWrite < newestDependency then
        yield source, output }


  /// Skip all files whose name starts with any string in the exclusion list
  let SkipExcludedFiles exclusions (files:seq<string * string>) = seq {
    for file, output in files do
      let fileNorm = System.Uri(file).LocalPath.ToLower()
      let excl = exclusions |> Seq.exists (fun (excl:string) -> 
        let excl = System.Uri(excl).LocalPath.ToLower()
        fileNorm.StartsWith(excl))
      if not excl then yield file, output }

  /// If the output file is in some subdirectory, then generate /blog/foo.index.html
  /// For files in the root, generate just /index.html (etc.)
  let TransformOutputFiles (output:string) files = seq {
    for file, (target:string) in files ->
      let relativeOut = target.Substring(output.Length + 1)
      // If it is not index & it is not in the root directory, then make it a sub-dir
      if not (relativeOut.EndsWith("index")) && (relativeOut.Contains("\\") || relativeOut.Contains("/"))
      then file, output ++ relativeOut ++ "index.html"
      else file, output ++ (relativeOut + ".html") }
      
      
// --------------------------------------------------------------------------------------
// Parsing blog posts etc.
// --------------------------------------------------------------------------------------
module BlogPosts = 

  open FileHelpers
  open System.Text.RegularExpressions

  /// Type that stores information about blog posts
  type BlogHeader = 
    { Title : string
      Abstract : string
      Description : string
      Date : System.DateTime
      Url : string
      Tags : seq<string> }

  /// Get all *.cshtml, *.html, *.md and *.fsx files in the blog directory
  let GetBlogFiles blog = seq {
    let exts = set [ ".md"; ".fsx"; ".cshtml"; ".html" ]
    let dirs = Array.append [| blog |] (Directory.GetDirectories(blog))
    for dir in dirs do
      if Path.GetFileNameWithoutExtension(dir) <> "abstracts" then
        for file in Directory.GetFiles(dir) do
          if exts |> Set.contains (Path.GetExtension(file).ToLower()) then
            if Path.GetFileNameWithoutExtension(file) <> "index" then
              yield file }
  
  let scriptHeaderRegex = 
    Regex("^\(\*\@(?<header>[^\*]*)\*\)(?<content>.*)$", RegexOptions.Singleline)
  let razorHeaderRegex = 
    Regex("^\@{(?<header>[^\*]*)}(?<content>.*)$", RegexOptions.Singleline)

  /// An FSX file must start with a header (*@ ... *) which is removed 
  /// before Literate processing (and then added back as @{ ... }
  let RemoveScriptHeader ext file = 
    let content = File.ReadAllText(file)
    let reg = (match ext with | ".fsx" -> scriptHeaderRegex | _ -> razorHeaderRegex).Match(content)
    if not reg.Success then 
      failwithf "The following F# script or Markdown file is missing a header:\n%s" file  
    let header = reg.Groups.["header"].Value
    let body = reg.Groups.["content"].Value
    "@{" + header + "}\n", body

  /// Return the header block of any blog post file
  let GetBlogHeaderAndAbstract transformer file =
    let regex =
      match Path.GetExtension(file).ToLower() with
      | ".fsx" -> scriptHeaderRegex
      | ".md" | ".html" | ".cshtml" -> razorHeaderRegex
      | _ -> failwith "File format not supported!"
    let reg = regex.Match(File.ReadAllText(file))
    if not reg.Success then 
      failwithf "The following source file is missing a header:\n%s" file  

    // Read abstract file and transform it
    let abstr = transformer (Path.GetDirectoryName(file) ++ "abstracts" ++ Path.GetFileName(file))
    file, reg.Groups.["header"].Value, abstr

  /// Simple function that parses the header of the blog post. Everybody knows
  /// that doing this with regexes is silly, but the blog post headers are simple enough.
  let ParseBlogHeader (blog:string) =
    let concatRegex = Regex("\"[\s]*\+[\s]*\"", RegexOptions.Compiled)
    fun (file:string, header:string, abstr) ->
      let lookup =
        header.Split(';')
        |> Array.filter (System.String.IsNullOrWhiteSpace >> not)
        |> Array.map (fun (s:string) -> 
            match s.Trim().Split('=') |> List.ofSeq with
            | key::values -> 
                let value = String.concat "=" values
                key.Trim(), concatRegex.Replace(value.Trim(' ', '\t', '\n', '\r', '"'), "")
            | _ -> failwithf "Invalid header in the following blog file: %s" file ) |> dict
      let relativeFile = file.Substring(blog.Length + 1)
      let relativeFile = let idx = relativeFile.LastIndexOf('.') in relativeFile.Substring(0, idx)
      try
        { Title = lookup.["Title"]
          Url = relativeFile
          Abstract = abstr
          Description = lookup.["Description"]
          Tags = lookup.["Tags"].Split([|','|], System.StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun s -> s.Trim())
          Date = lookup.["Date"] |> System.DateTime.Parse }
      with _ -> failwithf "Invalid header in the following blog file: %s" file

  /// Loads information about all blog posts
  let LoadBlogPosts transformer blog =
    GetBlogFiles blog 
    |> Seq.map ((GetBlogHeaderAndAbstract transformer) >> (ParseBlogHeader blog))
    |> Seq.sortBy (fun b -> b.Date)
    |> Array.ofSeq 
    |> Array.rev

// --------------------------------------------------------------------------------------
// Blog - the main blog functionality
// --------------------------------------------------------------------------------------

module Blog = 
  open BlogPosts
  open FileHelpers
  open System.Xml.Linq

  /// Represents the model that is passed to all pages
  type Model = 
    { Posts : BlogHeader[] 
      MonthlyPosts : (int * string * seq<BlogHeader>)[]
      Root : string }

  /// Walks over all blog post files and loads model (caches abstracts along the way)
  let LoadModel(transformer, (root:string), blog) = 
    let posts = LoadBlogPosts transformer blog
    let uk = System.Globalization.CultureInfo.GetCultureInfo("en-GB")
    { Posts = posts
      MonthlyPosts = 
        query { for p in posts do
                groupBy (p.Date.Year, p.Date.Month) into g
                let year, month = g.Key
                sortByDescending (year, month)
                select (year, uk.DateTimeFormat.GetMonthName(month), g :> seq<_>) }
        |> Array.ofSeq
      Root = root.Replace('\\', '/') }

  let TransformFile template hasHeader (razor:TildeLib.Razor) current target = 
    match Path.GetExtension(current).ToLower() with
    | (".fsx" | ".md") as ext ->
        let header, content = 
          if not hasHeader then "", File.ReadAllText(current)
          else RemoveScriptHeader ext current
        use fsx = DisposableFile.Create(current.Replace(ext, "_" + ext))
        use html = DisposableFile.CreateTemp(".html")
        File.WriteAllText(fsx.FileName, content)
        if ext = ".fsx" then
          Literate.ProcessScriptFile(fsx.FileName, template, html.FileName)
        else
          Literate.ProcessMarkdown(fsx.FileName, template, html.FileName)
        let processed = File.ReadAllText(html.FileName)
        File.WriteAllText(html.FileName, header + processed)
        EnsureDirectory(Path.GetDirectoryName(target))
        razor.ProcessFile(html.FileName, target)
    | ".html" | ".cshtml" ->
        razor.ProcessFile(current, target)
    | _ -> failwith "Not supported file!"

  let TransformAsTemp (template, source:string) razor current = 
    let cached = (Path.GetDirectoryName(current) ++ "cached" ++ Path.GetFileName(current))
    if File.Exists(cached) && 
      (File.GetLastWriteTime(cached) > File.GetLastWriteTime(current)) then 
      File.ReadAllText(cached)
    else
      printfn "Processing abstract: %s" (current.Substring(source.Length + 1))
      EnsureDirectory(Path.GetDirectoryName(current) ++ "cached")
      TransformFile template false razor current cached
      File.ReadAllText(cached)

  let GenerateRss root title description model target = 
    let (!) name = XName.Get(name)
    let items = 
      [| for item in model.Posts |> Seq.take 20 ->
           XElement
            ( !"item", 
              XElement(!"title", item.Title),
              XElement(!"guid", root + "/blog/" + item.Url),
              XElement(!"link", root + "/blog/" + item.Url + "/index.html"),
              XElement(!"pubDate", item.Date.ToUniversalTime().ToString("r")),
              XElement(!"description", item.Abstract) ) |]
    let channel = 
      XElement
        ( !"channel",
          XElement(!"title", (title:string)),
          XElement(!"link", (root:string)),
          XElement(!"description", (description:string)),
          items )
    let doc = XDocument(XElement(!"rss", XAttribute(!"version", "2.0"), channel))
    File.WriteAllText(target, doc.ToString())

// --------------------------------------------------------------------------------------
// Generating calendar
// --------------------------------------------------------------------------------------

module Calendar = 
  open Blog
  open BlogPosts
  open FileHelpers
  open System.Drawing
  open System.Drawing.Imaging

  type CalendarIndexModel =
    { Root : string 
      Posts : BlogHeader[] 
      MonthlyPosts : (int * string * seq<BlogHeader>)[]
      // More things needed by calendar index
      Year : int
      Months : seq<seq<string>> }

  type CalendarMonthModel =
    { Root : string 
      Posts : BlogHeader[] 
      MonthlyPosts : (int * string * seq<BlogHeader>)[]
      // More things needed by calendar page
      Title : string
      Link : string
      Days : seq<bool * int> }

  // Get objects needed for JPEG encoding
  let JpegCodec = 
    ImageCodecInfo.GetImageEncoders() 
    |> Seq.find (fun c -> c.FormatID = ImageFormat.Jpeg.Guid)
  let JpegEncoder = Encoder.Quality
  let QualityParam = new EncoderParameters(Param = [| new EncoderParameter(JpegEncoder, 95L) |])

  /// Resize file so that both width & height are smaller than 'maxSize'
  let ResizeFile maxSize source (target:string) = 
    use bmp = Bitmap.FromFile(source)
    let scale = max ((float bmp.Width) / (float maxSize)) ((float bmp.Height) / (float maxSize))
    use nbmp = new Bitmap(int (float bmp.Width / scale), int (float bmp.Height / scale))
    ( use gr = Graphics.FromImage(nbmp)
      gr.DrawImage(bmp, 0, 0, nbmp.Width, nbmp.Height) )
    nbmp.Save(target, JpegCodec, QualityParam)

  let GenerateCalendar root layouts output dependencies calendar calendarMonth calendarIndex (model:Model) = 
    let newestDependency = dependencies |> List.map Directory.GetLastWriteTime |> List.max
    let uk = System.Globalization.CultureInfo.GetCultureInfo("en-GB")
    let table f = seq { for row in 0 .. 3 -> seq { for col in 0 .. 2 -> f (row * 3 + col + 1) }}

    // Generate index HTML files with links to individual month files
    for dir in Directory.GetDirectories(calendar) do
      let year = int (Path.GetFileNameWithoutExtension(dir))
      let target = output ++ "calendar" ++ (string year) ++ "index.html"
      if not (File.Exists(target)) || ( Directory.GetLastWriteTime(target) < newestDependency) then
        // Generate index for the current year, because it is missing
        printfn "Generating calendar index: %d" year
        let index = 
          { CalendarIndexModel.Root = root; Year = year;
            Posts = model.Posts; MonthlyPosts = model.MonthlyPosts;
            Months = table uk.DateTimeFormat.GetMonthName }
        let razor = TildeLib.Razor(layouts, Model = index)
        EnsureDirectory (Path.GetDirectoryName(target))
        TransformFile "" false razor calendarIndex target
        
        // Generate individual calendar files
        for month in 1 .. 12 do
          let name = uk.DateTimeFormat.GetMonthName(month)
          let target = output ++ "calendar" ++ (string year) ++ (name.ToLower() + ".html")
          let days = seq { 
            for i in 1 .. uk.Calendar.GetDaysInMonth(year, month) ->
              uk.Calendar.GetDayOfWeek(DateTime(year, month, i)) = DayOfWeek.Sunday, i }                    
          let month = 
            { CalendarMonthModel.Root = root; Title = name + " " + (string year);
              Posts = model.Posts; MonthlyPosts = model.MonthlyPosts;
              Link = name.ToLower() + ".jpg"; Days = days }
          let razor = TildeLib.Razor(layouts, Model = month)
          EnsureDirectory (Path.GetDirectoryName(target))
          TransformFile "" false razor calendarMonth target
        

    // Generate all calendar files (resize appropriately)
    for dir in Directory.GetDirectories(calendar) do
      let year = int (Path.GetFileNameWithoutExtension(dir))
      let yearDir = output ++ "calendar" ++ (string year)
      printfn "Checking calendar files for: %d" year
      for month in 1 .. 12 do 
        let file = calendar ++ (string year) ++ (uk.DateTimeFormat.GetMonthName(month).ToLower() + ".jpg")
        let source = if File.Exists(file) then file else calendar ++ "na.jpg"
        let writeFile size suffix = 
          let target = yearDir ++ (Path.GetFileNameWithoutExtension(file) + suffix + ".jpg")
          if not (File.Exists(target)) || 
            (File.GetLastWriteTime(target) < File.GetLastWriteTime(source)) 
            then ResizeFile size source target
        writeFile 2400 "-original"
        writeFile 700 ""
        writeFile 240 "-preview"

// --------------------------------------------------------------------------------------
// Configuration
// --------------------------------------------------------------------------------------

open Blog
open BlogPosts
open FileHelpers
open Calendar

// Root URL for the generated HTML
let root = "http://tomasp.net" 
//let root = @"file:///C:\Tomas\Projects\WebSites\TomaspNet.New\output"

let title = "Tomas Petricek's blog"
let description = 
   "Writing about software development in F# and .NET, sharing materials from " +
   "my F# trainings and talks, writing about programming language research and theory."

// Information about source directory, blog subdirectory, layouts & content
let source = __SOURCE_DIRECTORY__ ++ "../source"
let blog = __SOURCE_DIRECTORY__ ++ "../source/blog"
let blogIndex = __SOURCE_DIRECTORY__ ++ "../source/blog/index.cshtml"
let layouts = __SOURCE_DIRECTORY__ ++ "../layouts"
let content = __SOURCE_DIRECTORY__ ++ "../content"
let template = __SOURCE_DIRECTORY__ ++ "empty-template.html"

let calendar = __SOURCE_DIRECTORY__ ++ "../calendar"
let calendarMonth = __SOURCE_DIRECTORY__ ++ "../source/calendar/month.cshtml"
let calendarIndex = __SOURCE_DIRECTORY__ ++ "../source/calendar/index.cshtml"

// F# code generation - skip 'exclude' directory & add 'references'
let exclude = 
  [ yield __SOURCE_DIRECTORY__ ++ "../source/blog/packages"
    yield __SOURCE_DIRECTORY__ ++ "../source/blog/abstracts"
    yield __SOURCE_DIRECTORY__ ++ "../source/calendar"
    for y in 2013 .. 2025 do
      yield __SOURCE_DIRECTORY__ ++ "../source/blog" ++ (string y) ++ "abstracts"  ]

let references = []

let special =
  [ source ++ "index.cshtml"
    source ++ "blog" ++ "index.cshtml" ]

// Output directory (gh-pages branch)
let output = __SOURCE_DIRECTORY__ ++ "../../output"

// Dependencies - if any of these files change, then we must regenerate all
let dependencies = 
  [ yield! Directory.GetFiles(layouts) 
    yield calendarMonth 
    yield calendarIndex ]
//   @ [ __SOURCE_DIRECTORY__ ++ __SOURCE_FILE__ ]


let clean() = 
  for dir in Directory.GetDirectories(output) do
    if not (dir.EndsWith(".git")) then SafeDeleteDir dir true
  for file in Directory.GetFiles(output) do
    File.Delete(file)

let build () =
  let razor = TildeLib.Razor(layouts, Model = { Model.Root = root; MonthlyPosts = [||]; Posts = [||] })
  let model = LoadModel(TransformAsTemp (template, source) razor, root, blog)
  
  // Generate RSS feed
  GenerateRss root title description model (output ++ "rss.xml")
  //GenerateCalendar root layouts output dependencies calendar calendarMonth calendarIndex model

  let uk = System.Globalization.CultureInfo.GetCultureInfo("en-GB")
  for year, month, posts in model.MonthlyPosts do
    let model = { model with Posts = Array.ofSeq posts }
    let razor = TildeLib.Razor(layouts, Model = model)
    let target =  output ++ "blog" ++ "archive" ++ (month.ToLower() + "-" + (string year)) ++ "index.html"
    EnsureDirectory(Path.GetDirectoryName(target))
    if not (File.Exists(target)) || 
        ( year = DateTime.Now.Year && 
          month = uk.DateTimeFormat.GetMonthName(DateTime.Now.Month) ) then
      printfn "Generating archive: %s %d" month year
      TransformFile template true razor blogIndex target

  let filesToProcess = 
    GetSourceFiles source output
    |> SkipExcludedFiles exclude
    |> TransformOutputFiles output
    |> FilterChangedFiles dependencies special
    
  let razor = TildeLib.Razor(layouts, Model = model)
  for current, target in filesToProcess do
    EnsureDirectory(Path.GetDirectoryName(target))
    printfn "Processing file: %s" (current.Substring(source.Length + 1))
    TransformFile template true razor current target

  CopyFiles content output 
  CopyFiles calendar (output ++ "calendar")

let server : ref<option<HttpServer>> = ref None

let stop () =
  server.Value |> Option.iter (fun v -> v.Stop())

let run() =
  let url = "http://localhost:8080/" 
  stop ()
  server := Some(HttpServer.Start(url, output, Replacements = ["http://tomasp.net/", url]))
  printfn "Starting web server at %s" url
  System.Diagnostics.Process.Start(url) |> ignore

run ()
stop ()

build()
clean()

