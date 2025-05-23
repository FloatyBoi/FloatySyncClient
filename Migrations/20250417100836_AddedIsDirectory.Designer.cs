﻿// <auto-generated />
using System;
using FloatySyncClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace FloatySyncClient.Migrations
{
    [DbContext(typeof(SyncDbContext))]
    [Migration("20250417100836_AddedIsDirectory")]
    partial class AddedIsDirectory
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "9.0.4");

            modelBuilder.Entity("FloatySyncClient.FileMetadata", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("Checksum")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("GroupId")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("LastModifiedUtc")
                        .HasColumnType("TEXT");

                    b.Property<string>("RelativePath")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("StoredPathOnClient")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<bool>("isDirectory")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.ToTable("Files");
                });

            modelBuilder.Entity("FloatySyncClient.Group", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<int>("IdOnServer")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Key")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("LastSyncUtc")
                        .HasColumnType("TEXT");

                    b.Property<string>("LocalFolder")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("Groups");
                });
#pragma warning restore 612, 618
        }
    }
}
