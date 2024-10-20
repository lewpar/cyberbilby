﻿using CyberBilby.MgmtServer.Network;

using CyberBilby.Shared.Network;
using CyberBilby.Shared.Security;
using CyberBilby.Shared.Extensions;
using CyberBilby.Shared.Repositories;
using CyberBilby.Shared.Database.Entities;
using CyberBilby.Shared.Network.Packets;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using System.Net;
using System.Net.Security;
using System.Net.Sockets;

using System.Security.Cryptography.X509Certificates;

namespace CyberBilby.MgmtServer.Services;

public class ManagementServiceHost : BackgroundService
{
    private TcpListener _listener;
    private readonly ILogger<ManagementServiceHost> logger;
    private readonly IBlogRepository blogRepo;
    private List<SslClient> authenticatedClients;
    private CancellationToken stoppingToken;
    private Dictionary<PacketType, Func<SslClient, Task>> packetHandlers;

    public ManagementServiceHost(ILogger<ManagementServiceHost> logger, IBlogRepository blogRepo)
    {
        _listener = new TcpListener(IPAddress.Any, 44123);

        this.logger = logger;
        this.blogRepo = blogRepo;

        authenticatedClients = new List<SslClient>();
        packetHandlers = new Dictionary<PacketType, Func<SslClient, Task>>()
        {
            { PacketType.CMSG_GET_POSTS, HandleRequestPostsAsync },
            { PacketType.CMSG_CREATE_POST, HandleCreatePostAsync }
        };
    }

    private async Task StartListeningAsync()
    {
        logger.LogInformation("Starting listen server..");

        _listener.Start();

        logger.LogInformation("Started. Waiting for clients..");

        while(!stoppingToken.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync();

            _ = HandleClientConnectAsync(client);

            logger.LogInformation($"Client '{client.Client.RemoteEndPoint}' connected.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this.stoppingToken = stoppingToken;

        await StartListeningAsync();
    }

    private async Task<bool> ValidateCertificateAsync(X509Certificate2? certificate)
    {
        if(certificate is null)
        {
            return false;
        }

        var isRevoked = await blogRepo.IsCertificateRevokedAsync(certificate.Thumbprint);
        if(isRevoked)
        {
            return false;
        }

        var author = await blogRepo.GetAuthorAsync(certificate.Thumbprint);
        if (author is null)
        {
            return false;
        }

        return true;
    }

    private async Task HandleClientConnectAsync(TcpClient client)
    {
        try
        {
            var sslStream = new SslStream(client.GetStream(), false);
            await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions()
            {
                ServerCertificate = X509Cert2.LoadFromFile("./server.pfx"),
                ClientCertificateRequired = true
            });

            var certificate = sslStream.RemoteCertificate as X509Certificate2;
            if (certificate is null)
            {
                return;
            }

            if(!await ValidateCertificateAsync(certificate))
            {
                sslStream.Close();
                return;
            }

            var author = await blogRepo.GetAuthorAsync(certificate.Thumbprint);
            if (author is null)
            {
                sslStream.Close();
                return;
            }

            sslStream.SendPacket(new AuthenticateResponsePacket()
            {
                Profile = new AuthProfile()
                {
                    Name = author.Name,
                    Role = author.Role
                }
            });

            var sslClient = new SslClient()
            {
                Endpoint = client.Client.RemoteEndPoint,
                Fingerprint = certificate.Thumbprint,
                Stream = sslStream,
                Client = client
            };

            authenticatedClients.Add(sslClient);

            logger.LogInformation("Client passed authentication.");

            await ListenForDataAsync(sslClient);
        }
        catch(Exception ex)
        {
            logger.LogCritical($"Client failed authentication: {ex.Message} {ex.StackTrace}");
        }
    }

    private async Task ListenForDataAsync(SslClient client)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            var stream = client.Stream;

            var packetType = (PacketType)await stream.ReadIntAsync(stoppingToken);
            if(!packetHandlers.ContainsKey(packetType))
            {
                await client.DisconnectAsync("Invalid packet type.");
                break;
            }

            await packetHandlers[packetType].Invoke(client);
        }
    }

    private async Task HandleRequestPostsAsync(SslClient client)
    {
        logger.LogInformation($"Client '{client.Endpoint}' is requesting posts.");

        var posts = await blogRepo.GetAllPostsAsync();
        var response = new GetPostsResponsePacket()
        {
            Posts = posts.ToList()
        };

        await client.Stream.SendPacketAsync(response);
    }

    private async Task HandleCreatePostAsync(SslClient client)
    {
        logger.LogInformation($"Client '{client.Endpoint}' is creating post.");

        var post = await client.Stream.DeserializeAsync<BlogPost>();
        if(post is null)
        {
            logger.LogCritical("Failed to deserialize blog post.");

            await client.Stream.SendPacketAsync(new CreatePostResponsePacket() 
            { 
                Result = false, 
                Message = "Internal server error occured" 
            });

            return;
        }

        post.Author = await blogRepo.GetAuthorAsync(client.Fingerprint);

        if(await blogRepo.PostWithSlugExistsAsync(post))
        {
            await client.Stream.SendPacketAsync(new CreatePostResponsePacket()
            {
                Result = false,
                Message = "A post with that slug already exists."
            });

            return;
        }

        await blogRepo.CreatePostAsync(post);
        await client.Stream.SendPacketAsync(new CreatePostResponsePacket() 
        { 
            Result = true,
            Message = "Post created."
        });
    }
}