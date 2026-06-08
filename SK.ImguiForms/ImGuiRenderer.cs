namespace SK.ImguiForms {
    using Hexa.NET.ImGui;
    using ImDrawIdx = System.UInt16;
    using Vortice.DXGI;
    using Vortice.Direct3D;
    using Vortice.Direct3D11;
    using Vortice.D3DCompiler;
    using Vortice.Mathematics;
    using System.Numerics;
    using System.Collections.Generic;
    using System;
    using System.Linq;
    using SixLabors.ImageSharp;
    using SixLabors.ImageSharp.PixelFormats;
    using System.Buffers;

    unsafe internal sealed class ImGuiRenderer : IDisposable {
        internal readonly struct FrameDiagnostics {
            public int CmdListsCount { get; init; }

            public int TotalVtxCount { get; init; }

            public int TotalIdxCount { get; init; }

            public Vector2 DisplaySize { get; init; }

            public IntPtr FontTextureId { get; init; }

            public bool FontTextureRegistered { get; init; }
        }

        // Dear ImGui 1.92 dynamic textures: each texture (font atlas, user image) is a GPU resource we
        // create/update/destroy on demand from ImDrawData.Textures. We keep both the texture and its SRV —
        // the SRV is bound at draw time; the texture is needed for partial UpdateSubresource uploads.
        sealed class TextureEntry {
            public ID3D11Texture2D Texture;
            public ID3D11ShaderResourceView Srv;
        }

        const int VertexConstantBufferSize = 16 * 4;
        const int MaxTextureDimension = 16384; // D3D11 feature level 11 max (2D texture U/V)
        readonly ImGuiContextPtr context;

        ID3D11Device device;
        ID3D11DeviceContext deviceContext;
        ID3D11Buffer vertexBuffer;
        ID3D11Buffer indexBuffer;
        Blob vertexShaderBlob;
        ID3D11VertexShader vertexShader;
        ID3D11InputLayout inputLayout;
        ID3D11Buffer constantBuffer;
        Blob pixelShaderBlob;
        ID3D11PixelShader pixelShader;
        ID3D11SamplerState fontSampler;
        ID3D11RasterizerState rasterizerState;
        ID3D11BlendState blendState;
        ID3D11DepthStencilState depthStencilState;
        int vertexBufferSize = 5000, indexBufferSize = 10000;
        readonly Dictionary<IntPtr, TextureEntry> textureResources = new();

        public ImGuiRenderer(ID3D11Device device, ID3D11DeviceContext deviceContext, int width, int height) {
            this.device = device;
            this.deviceContext = deviceContext;

            device.AddRef();
            deviceContext.AddRef();

            lock(ImGuiContextSync.SyncRoot) {
                context = ImGui.CreateContext();
                ImGui.SetCurrentContext(context);
                var io = ImGui.GetIO();
                io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;  // honor ImDrawCmd::VtxOffset (large meshes)
                io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;   // 1.92 dynamic texture management
                io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
                io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
                var platformIo = ImGui.GetPlatformIO();
                platformIo.RendererTextureMaxWidth = MaxTextureDimension;
                platformIo.RendererTextureMaxHeight = MaxTextureDimension;
                ImGui.StyleColorsDark();
                this.Resize(width, height);
                this.CreateDeviceObjects();
            }
        }

        public ImGuiContextPtr Context => context;

        public void MakeCurrent() {
            if(ImGui.GetCurrentContext().Handle != context.Handle) {
                ImGui.SetCurrentContext(context);
            }
        }

        public void Update(float deltaTime, Action DoRender) {
            lock(ImGuiContextSync.SyncRoot) {
                MakeCurrent();
                var io = ImGui.GetIO();
                io.DeltaTime = deltaTime;
                ImGui.NewFrame();
                DoRender?.Invoke();
                ImGui.Render();
            }
        }

        public void Render() {
            lock(ImGuiContextSync.SyncRoot) {
                MakeCurrent();
                ImDrawDataPtr data = ImGui.GetDrawData();
                // Avoid rendering when minimized
                if(data.DisplaySize.X <= 0.0f || data.DisplaySize.Y <= 0.0f)
                    return;

                ID3D11DeviceContext ctx = deviceContext;

                // 1.92: service pending texture create/update/destroy requests before drawing.
                var textures = data.Textures;
                if((nint)textures.Data != 0) {
                    for(int i = 0; i < textures.Size; i++) {
                        var tex = textures.Data[i];
                        if(tex.Status != ImTextureStatus.Ok) {
                            UpdateTexture(tex);
                        }
                    }
                }

                if(vertexBuffer == null || vertexBufferSize < data.TotalVtxCount) {
                    vertexBuffer?.Release();

                    vertexBufferSize = data.TotalVtxCount + 5000;
                    var desc = new BufferDescription(
                        vertexBufferSize * sizeof(ImDrawVert),
                        BindFlags.VertexBuffer,
                        ResourceUsage.Dynamic,
                        CpuAccessFlags.Write);
                    vertexBuffer = device.CreateBuffer(desc);
                }

                if(indexBuffer == null || indexBufferSize < data.TotalIdxCount) {
                    indexBuffer?.Release();

                    indexBufferSize = data.TotalIdxCount + 10000;

                    var desc = new BufferDescription(
                        indexBufferSize * sizeof(ImDrawIdx),
                        BindFlags.IndexBuffer,
                        ResourceUsage.Dynamic,
                        CpuAccessFlags.Write);

                    indexBuffer = device.CreateBuffer(desc);
                }

                // Upload vertex/index data into a single contiguous GPU buffer
                var vertexResource = ctx.Map(vertexBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                var indexResource = ctx.Map(indexBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                var vertexResourcePointer = (ImDrawVert*)vertexResource.DataPointer;
                var indexResourcePointer = (ImDrawIdx*)indexResource.DataPointer;
                for(int n = 0; n < data.CmdListsCount; n++) {
                    var cmdlList = data.CmdLists.Data[n];

                    var vertBytes = cmdlList.VtxBuffer.Size * sizeof(ImDrawVert);
                    Buffer.MemoryCopy((void*)cmdlList.VtxBuffer.Data, vertexResourcePointer, vertBytes, vertBytes);

                    var idxBytes = cmdlList.IdxBuffer.Size * sizeof(ImDrawIdx);
                    Buffer.MemoryCopy((void*)cmdlList.IdxBuffer.Data, indexResourcePointer, idxBytes, idxBytes);

                    vertexResourcePointer += cmdlList.VtxBuffer.Size;
                    indexResourcePointer += cmdlList.IdxBuffer.Size;
                }
                ctx.Unmap(vertexBuffer, 0);
                ctx.Unmap(indexBuffer, 0);

                // Setup orthographic projection matrix into our constant buffer
                var constResource = ctx.Map(constantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                var span = constResource.AsSpan<float>(VertexConstantBufferSize);
                float L = data.DisplayPos.X;
                float R = data.DisplayPos.X + data.DisplaySize.X;
                float T = data.DisplayPos.Y;
                float B = data.DisplayPos.Y + data.DisplaySize.Y;
                float[] mvp =
                {
                    2.0f/(R-L),   0.0f,           0.0f,       0.0f,
                    0.0f,         2.0f/(T-B),     0.0f,       0.0f,
                    0.0f,         0.0f,           0.5f,       0.0f,
                    (R+L)/(L-R),  (T+B)/(B-T),    0.5f,       1.0f,
            };
                mvp.CopyTo(span);
                ctx.Unmap(constantBuffer, 0);
                SetupRenderState(data, ctx);
                // Render command lists (single merged buffer, maintain our own offsets)
                int global_idx_offset = 0;
                int global_vtx_offset = 0;
                for(int n = 0; n < data.CmdListsCount; n++) {
                    var cmdList = data.CmdLists.Data[n];
                    for(int i = 0; i < cmdList.CmdBuffer.Size; i++) {
                        var cmd = cmdList.CmdBuffer.Data[i];
                        if(cmd.UserCallback != null) {
                            throw new NotImplementedException("user callbacks not implemented");
                        }
                        else {
                            var clipMinX = (int)MathF.Floor(cmd.ClipRect.X - data.DisplayPos.X);
                            var clipMinY = (int)MathF.Floor(cmd.ClipRect.Y - data.DisplayPos.Y);
                            var clipMaxX = (int)MathF.Ceiling(cmd.ClipRect.Z - data.DisplayPos.X);
                            var clipMaxY = (int)MathF.Ceiling(cmd.ClipRect.W - data.DisplayPos.Y);

                            if(clipMaxX <= clipMinX || clipMaxY <= clipMinY) {
                                continue;
                            }

                            ctx.RSSetScissorRect(new Vortice.RawRect(clipMinX, clipMinY, clipMaxX, clipMaxY));

                            if(textureResources.TryGetValue((nint)cmd.GetTexID().Handle, out var entry)) {
                                ctx.PSSetShaderResource(0, entry.Srv);
                            }

                            ctx.DrawIndexed((int)cmd.ElemCount, (int)(cmd.IdxOffset + global_idx_offset), (int)(cmd.VtxOffset + global_vtx_offset));
                        }
                    }
                    global_idx_offset += cmdList.IdxBuffer.Size;
                    global_vtx_offset += cmdList.VtxBuffer.Size;
                }
            }
        }

        // Implements the Dear ImGui 1.92 backend texture protocol (see imgui_impl_dx11.cpp).
        void UpdateTexture(ImTextureDataPtr tex) {
            if(tex.Status == ImTextureStatus.WantCreate) {
                var texDesc = new Texture2DDescription(Format.R8G8B8A8_UNorm, tex.Width, tex.Height, 1, 1);
                var subResource = new SubresourceData((IntPtr)tex.GetPixels(), tex.Width * 4);
                var texture = device.CreateTexture2D(texDesc, new[] { subResource });
                var resViewDesc = new ShaderResourceViewDescription(texture, ShaderResourceViewDimension.Texture2D, Format.R8G8B8A8_UNorm, 0, texDesc.MipLevels);
                var srv = device.CreateShaderResourceView(texture, resViewDesc);
                var id = RegisterTexture(texture, srv);
                tex.SetTexID(new ImTextureID(id));
                tex.SetStatus(ImTextureStatus.Ok);
            }
            else if(tex.Status == ImTextureStatus.WantUpdates) {
                if(textureResources.TryGetValue((nint)tex.GetTexID().Handle, out var entry) && entry.Texture != null) {
                    var updates = tex.Updates;
                    var pitch = tex.GetPitch();
                    for(int u = 0; u < updates.Size; u++) {
                        var r = updates.Data[u];
                        var box = new Box(r.X, r.Y, 0, r.X + r.W, r.Y + r.H, 1);
                        deviceContext.UpdateSubresource(entry.Texture, 0, box, (IntPtr)tex.GetPixelsAt(r.X, r.Y), pitch, 0);
                    }
                }
                tex.SetStatus(ImTextureStatus.Ok);
            }
            else if(tex.Status == ImTextureStatus.WantDestroy && tex.UnusedFrames > 0) {
                DeRegisterTexture((nint)tex.GetTexID().Handle);
                tex.SetTexID(ImTextureID.Null);
                tex.SetStatus(ImTextureStatus.Destroyed);
            }
        }

        public void Dispose() {
            if(device == null)
                return;

            this.DeRegisterAllTexture();
            fontSampler?.Release();
            indexBuffer?.Release();
            vertexBuffer?.Release();
            blendState?.Release();
            depthStencilState?.Release();
            rasterizerState?.Release();
            pixelShader?.Release();
            pixelShaderBlob?.Release();
            constantBuffer?.Release();
            inputLayout?.Release();
            vertexShader?.Release();
            vertexShaderBlob?.Release();
        }

        public void Resize(int width, int height) {
            lock(ImGuiContextSync.SyncRoot) {
                MakeCurrent();
                ImGui.GetIO().DisplaySize = new Vector2(width, height);
            }
        }

        public FrameDiagnostics CaptureFrameDiagnostics() {
            lock(ImGuiContextSync.SyncRoot) {
                MakeCurrent();
                var drawData = ImGui.GetDrawData();
                var hasDrawData = (nint)drawData.Handle != 0;
                return new FrameDiagnostics {
                    CmdListsCount = hasDrawData ? drawData.CmdListsCount : 0,
                    TotalVtxCount = hasDrawData ? drawData.TotalVtxCount : 0,
                    TotalIdxCount = hasDrawData ? drawData.TotalIdxCount : 0,
                    DisplaySize = hasDrawData ? drawData.DisplaySize : Vector2.Zero,
                    FontTextureId = IntPtr.Zero,
                    FontTextureRegistered = textureResources.Count > 0
                };
            }
        }

        public IntPtr CreateImageTexture(Image<Rgba32> image, Format format) {
            var texDesc = new Texture2DDescription(format, image.Width, image.Height, 1, 1);
            if(!image.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> memory)) {
                throw new Exception("Make sure to initialize MemoryAllocator.Default!");
            }

            using MemoryHandle imageMemoryHandle = memory.Pin();
            var subResource = new SubresourceData(imageMemoryHandle.Pointer, texDesc.Width * 4);
            var texture = device.CreateTexture2D(texDesc, new[] { subResource });
            var resViewDesc = new ShaderResourceViewDescription(texture, ShaderResourceViewDimension.Texture2D, format, 0, texDesc.MipLevels);
            return RegisterTexture(texture, device.CreateShaderResourceView(texture, resViewDesc));
        }

        public bool RemoveImageTexture(IntPtr handle) {
            return DeRegisterTexture(handle);
        }

        // Loads/replaces the font set in this context's atlas. The GPU texture itself is created lazily by the
        // per-frame texture loop above (1.92), so no atlas rasterization happens here.
        public void UpdateFontTexture(FontHelper.FontLoadDelegate fontLoadFunc) {
            lock(ImGuiContextSync.SyncRoot) {
                MakeCurrent();
                var io = ImGui.GetIO();
                io.Fonts.Clear();
                var config = ImGui.ImFontConfig();
                config.PixelSnapH = true;
                config.OversampleH = 2;
                config.OversampleV = 1;
                fontLoadFunc(config);
                config.Destroy();
            }
        }

        void SetupRenderState(ImDrawDataPtr drawData, ID3D11DeviceContext ctx) {
            var viewport = new Viewport(0f, 0f, drawData.DisplaySize.X, drawData.DisplaySize.Y, 0f, 1f);
            ctx.RSSetViewport(viewport);
            int stride = sizeof(ImDrawVert);
            ctx.IASetInputLayout(inputLayout);
            ctx.IASetVertexBuffer(0, vertexBuffer, stride);
            ctx.IASetIndexBuffer(indexBuffer, sizeof(ImDrawIdx) == 2 ? Format.R16_UInt : Format.R32_UInt, 0);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.VSSetShader(vertexShader);
            ctx.VSSetConstantBuffer(0, constantBuffer);
            ctx.PSSetShader(pixelShader);
            ctx.PSSetSampler(0, fontSampler);
            ctx.GSSetShader(null);
            ctx.HSSetShader(null);
            ctx.DSSetShader(null);
            ctx.CSSetShader(null);

            ctx.OMSetBlendState(blendState, new Color4(0f, 0f, 0f, 0f));
            ctx.OMSetDepthStencilState(depthStencilState);
            ctx.RSSetState(rasterizerState);
        }

        void CreateFontSampler() {
            var samplerDesc = new SamplerDescription(
                Filter.MinMagMipLinear,
                TextureAddressMode.Wrap,
                TextureAddressMode.Wrap,
                TextureAddressMode.Wrap,
                0f,
                0,
                ComparisonFunction.Always,
                0f,
                0f);

            this.fontSampler = device.CreateSamplerState(samplerDesc);
        }

        IntPtr RegisterTexture(ID3D11Texture2D texture, ID3D11ShaderResourceView srv) {
            var imguiID = srv.NativePointer;
            textureResources[imguiID] = new TextureEntry { Texture = texture, Srv = srv };
            return imguiID;
        }

        bool DeRegisterTexture(IntPtr texturePtr) {
            if(textureResources.Remove(texturePtr, out var entry)) {
                entry.Srv?.Release();
                entry.Texture?.Release();
                return true;
            }

            return false;
        }

        void DeRegisterAllTexture() {
            foreach(var key in textureResources.Keys.ToArray()) {
                this.DeRegisterTexture(key);
            }
        }

        void CreateDeviceObjects() {
            var vertexShaderCode =
                @"
                    cbuffer vertexBuffer : register(b0)
                    {
                        float4x4 ProjectionMatrix;
                    };

                    struct VS_INPUT
                    {
                        float2 pos : POSITION;
                        float4 col : COLOR0;
                        float2 uv  : TEXCOORD0;
                    };

                    struct PS_INPUT
                    {
                        float4 pos : SV_POSITION;
                        float4 col : COLOR0;
                        float2 uv  : TEXCOORD0;
                    };

                    PS_INPUT main(VS_INPUT input)
                    {
                        PS_INPUT output;
                        output.pos = mul(ProjectionMatrix, float4(input.pos.xy, 0.f, 1.f));
                        output.col = input.col;
                        output.uv  = input.uv;
                        return output;
                    }";
            Compiler.Compile(vertexShaderCode, "main", "vs", "vs_4_0", out vertexShaderBlob, out _);
            if(vertexShaderBlob == null)
                throw new Exception("error compiling vertex shader");

            vertexShader = device.CreateVertexShader(vertexShaderBlob);

            var inputElements = new[]
            {
                new InputElementDescription( "POSITION", 0, Format.R32G32_Float,   0, 0, InputClassification.PerVertexData, 0 ),
                new InputElementDescription( "TEXCOORD", 0, Format.R32G32_Float,   8,  0, InputClassification.PerVertexData, 0 ),
                new InputElementDescription( "COLOR",    0, Format.R8G8B8A8_UNorm, 16, 0, InputClassification.PerVertexData, 0 ),
            };

            inputLayout = device.CreateInputLayout(inputElements, vertexShaderBlob);

            var constBufferDesc = new BufferDescription(
                VertexConstantBufferSize,
                BindFlags.ConstantBuffer,
                ResourceUsage.Dynamic,
                CpuAccessFlags.Write);

            constantBuffer = device.CreateBuffer(constBufferDesc);

            var pixelShaderCode =
                @"struct PS_INPUT
                    {
                        float4 pos : SV_POSITION;
                        float4 col : COLOR0;
                        float2 uv  : TEXCOORD0;
                    };

                    sampler sampler0;
                    Texture2D texture0;

                    float4 main(PS_INPUT input) : SV_Target
                    {
                        return input.col * texture0.Sample(sampler0, input.uv);
                    }";
            Compiler.Compile(pixelShaderCode, "main", "ps", "ps_4_0", out pixelShaderBlob, out _);
            if(pixelShaderBlob == null)
                throw new Exception("error compiling pixel shader");

            pixelShader = device.CreatePixelShader(pixelShaderBlob);

            var blendDesc = new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha);
            blendState = device.CreateBlendState(blendDesc);

            var rasterDesc = new RasterizerDescription(CullMode.None, FillMode.Solid) {
                MultisampleEnable = false,
                ScissorEnable = true
            };
            rasterizerState = device.CreateRasterizerState(rasterDesc);

            var depthDesc = new DepthStencilDescription(false, DepthWriteMask.All, ComparisonFunction.Always);
            depthStencilState = device.CreateDepthStencilState(depthDesc);

            this.CreateFontSampler();
        }
    }

}
