using System;
using CtxUi.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace CtxUi.Core.Data.Migrations
{
    [DbContext(typeof(CtxUiDbContext))]
    [Migration("20261012203131_Initial")]
    partial class Initial
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "10.0.12");

            modelBuilder.Entity("CtxUi.Core.Clients.Client", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("TEXT");

                    b.Property<long>("CreatedAt")
                        .HasColumnType("INTEGER");

                    b.Property<bool>("Enabled")
                        .HasColumnType("INTEGER");

                    b.Property<long?>("ExpiresAt")
                        .HasColumnType("INTEGER");

                    b.Property<long?>("FirstUsedAt")
                        .HasColumnType("INTEGER");

                    b.Property<long?>("LastUsedAt")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Metadata")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("NameKey")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("TokenHash")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("TokenPrefix")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("TokenSealed")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("NameKey")
                        .IsUnique();

                    b.HasIndex("TokenHash")
                        .IsUnique();

                    b.ToTable("Clients");
                });

            modelBuilder.Entity("CtxUi.Core.Clients.ClientInbound", b =>
                {
                    b.Property<Guid>("ClientId")
                        .HasColumnType("TEXT");

                    b.Property<Guid>("InboundId")
                        .HasColumnType("TEXT");

                    b.HasKey("ClientId", "InboundId");

                    b.HasIndex("InboundId");

                    b.ToTable("ClientInbounds");
                });

            modelBuilder.Entity("CtxUi.Core.Data.RoutingState", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<long>("Revision")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.ToTable("RoutingStates");

                    b.HasData(
                        new
                        {
                            Id = 1,
                            Revision = 0L
                        });
                });

            modelBuilder.Entity("CtxUi.Core.Inbounds.Inbound", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("TEXT");

                    b.Property<long>("CreatedAt")
                        .HasColumnType("INTEGER");

                    b.Property<bool>("Enabled")
                        .HasColumnType("INTEGER");

                    b.Property<bool>("ForwardIdentity")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Kind")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int>("RequestTimeoutSeconds")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Slug")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("UpstreamHeadersSealed")
                        .HasColumnType("TEXT");

                    b.Property<string>("Url")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("Slug")
                        .IsUnique();

                    b.ToTable("Inbounds");
                });

            modelBuilder.Entity("CtxUi.Core.Clients.ClientInbound", b =>
                {
                    b.HasOne("CtxUi.Core.Clients.Client", null)
                        .WithMany("Inbounds")
                        .HasForeignKey("ClientId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("CtxUi.Core.Inbounds.Inbound", "Inbound")
                        .WithMany()
                        .HasForeignKey("InboundId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Inbound");
                });

            modelBuilder.Entity("CtxUi.Core.Clients.Client", b =>
                {
                    b.Navigation("Inbounds");
                });
#pragma warning restore 612, 618
        }
    }
}
