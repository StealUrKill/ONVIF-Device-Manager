namespace odm.core

open System
open System.IO
open System.ServiceModel.Channels
open System.Text
open System.Xml

/// Reads SOAP replies that have errors. Replaces incorrect UTF-8 bytes with U+FFFD before WCF
/// parses them, and reads multipart (MTOM) replies. Valid text replies do not change.
module internal Utf8Sanitizer =
    let private strict = new UTF8Encoding(false, true)
    let private lenient = new UTF8Encoding(false, false)

    /// Some(corrected bytes) if the input is not valid UTF-8, None if it is valid.
    let sanitize (bytes: byte[]) (offset: int) (count: int) : byte[] option =
        try
            strict.GetCharCount(bytes, offset, count) |> ignore
            None
        with :? DecoderFallbackException ->
            Some (lenient.GetBytes(lenient.GetString(bytes, offset, count)))

    /// Correct only UTF-8 text bodies. Do not change MTOM, multipart or other character sets.
    let appliesTo (contentType: string) =
        String.IsNullOrEmpty(contentType)
        || (contentType.IndexOf("multipart", StringComparison.OrdinalIgnoreCase) < 0
            && (contentType.IndexOf("charset", StringComparison.OrdinalIgnoreCase) < 0
                || contentType.IndexOf("utf-8", StringComparison.OrdinalIgnoreCase) >= 0))


type internal Utf8SanitizingMessageEncoder(inner: MessageEncoder, mtom: MessageEncoder) =
    inherit MessageEncoder()

    let isMultipart (contentType: string) =
        mtom <> null && not (String.IsNullOrEmpty(contentType))
        && contentType.TrimStart().StartsWith("multipart/related", StringComparison.OrdinalIgnoreCase)

    override this.ContentType = inner.ContentType
    override this.MediaType = inner.MediaType
    override this.MessageVersion = inner.MessageVersion
    override this.IsContentTypeSupported(contentType) =
        inner.IsContentTypeSupported(contentType) || isMultipart contentType
    override this.GetProperty<'T when 'T : not struct>() = inner.GetProperty<'T>()

    override this.ReadMessage(buffer: ArraySegment<byte>, bufferManager: BufferManager, contentType: string) =
      if isMultipart contentType then
        mtom.ReadMessage(buffer, bufferManager, contentType)
      else
        let fixedBytes =
            if Utf8Sanitizer.appliesTo contentType then Utf8Sanitizer.sanitize buffer.Array buffer.Offset buffer.Count
            else None
        match fixedBytes with
        | None -> inner.ReadMessage(buffer, bufferManager, contentType)
        | Some bytes ->
            bufferManager.ReturnBuffer(buffer.Array)
            let copy = bufferManager.TakeBuffer(bytes.Length)
            Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length)
            inner.ReadMessage(ArraySegment<byte>(copy, 0, bytes.Length), bufferManager, contentType)

    override this.ReadMessage(stream: Stream, maxSizeOfHeaders: int, contentType: string) =
        if isMultipart contentType then
            mtom.ReadMessage(stream, maxSizeOfHeaders, contentType)
        elif not (Utf8Sanitizer.appliesTo contentType) then
            inner.ReadMessage(stream, maxSizeOfHeaders, contentType)
        else
            // SOAP replies are small. Read all of the stream, so that ODM can examine it.
            let bytes =
                use ms = new MemoryStream()
                stream.CopyTo(ms)
                stream.Close()
                ms.ToArray()
            let data =
                match Utf8Sanitizer.sanitize bytes 0 bytes.Length with
                | Some fixedBytes -> fixedBytes
                | None -> bytes
            inner.ReadMessage(new MemoryStream(data), maxSizeOfHeaders, contentType)

    override this.WriteMessage(message: Message, maxMessageSize: int, bufferManager: BufferManager, messageOffset: int) =
        inner.WriteMessage(message, maxMessageSize, bufferManager, messageOffset)

    override this.WriteMessage(message: Message, stream: Stream) =
        inner.WriteMessage(message, stream)


type internal Utf8SanitizingMessageEncoderFactory(inner: MessageEncoderFactory) =
    inherit MessageEncoderFactory()
    // MTOM decoder for multipart replies, with the same message version as the text encoder.
    let mtom =
        let e = new MtomMessageEncodingBindingElement(inner.MessageVersion, Encoding.UTF8)
        e.ReaderQuotas.MaxStringContentLength <- Int32.MaxValue
        e.ReaderQuotas.MaxArrayLength <- Int32.MaxValue
        e.MaxBufferSize <- Int32.MaxValue
        e.CreateMessageEncoderFactory().Encoder
    let encoder = new Utf8SanitizingMessageEncoder(inner.Encoder, mtom)

    override this.Encoder = encoder :> MessageEncoder
    override this.MessageVersion = inner.MessageVersion
    override this.CreateSessionEncoder() =
        new Utf8SanitizingMessageEncoder(inner.CreateSessionEncoder(), mtom) :> MessageEncoder


/// Wraps a TextMessageEncodingBindingElement. ODM corrects the UTF-8 of the replies.
type internal Utf8SanitizingTextEncodingBindingElement(inner: TextMessageEncodingBindingElement) =
    inherit MessageEncodingBindingElement()

    override this.MessageVersion
        with get () = inner.MessageVersion
        and set (v) = inner.MessageVersion <- v

    override this.CreateMessageEncoderFactory() =
        new Utf8SanitizingMessageEncoderFactory(inner.CreateMessageEncoderFactory()) :> MessageEncoderFactory

    override this.Clone() =
        new Utf8SanitizingTextEncodingBindingElement(inner.Clone() :?> TextMessageEncodingBindingElement) :> BindingElement

    override this.CanBuildChannelFactory<'TChannel>(context: BindingContext) =
        context.CanBuildInnerChannelFactory<'TChannel>()

    override this.BuildChannelFactory<'TChannel>(context: BindingContext) =
        // The transport gets the encoder from the binding parameters.
        context.BindingParameters.Add(this)
        context.BuildInnerChannelFactory<'TChannel>()

    override this.GetProperty<'T when 'T : not struct>(context: BindingContext) =
        if typeof<'T> = typeof<XmlDictionaryReaderQuotas> then
            inner.GetProperty<'T>(context)
        else
            base.GetProperty<'T>(context)
