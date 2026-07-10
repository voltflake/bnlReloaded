# BlockNLoad Community Made Server
This is the open source github repo for the bnl private server recreation project.
Originally developed and made by @nascarman9, this is a fork of https://github.com/nascarman9/bnlReloaded

To connect to private server people mainly use launcher made by @devprbtt. It features a few ping compensation fixes and a lot of other optional features.
https://github.com/devprbtt/blocknload-community-launcher/releases/

To use the cdb serializer/deserializer, create a folder called Cache in the base directory (should be in the same directory as the BaseTypes, Database, etc. folders). Put the cdb file from the game assets into the new Cache folder. Then change the toJson and fromJson constants to serialize/deserialize the cdb to a json file/zipped cdb file respectively.
