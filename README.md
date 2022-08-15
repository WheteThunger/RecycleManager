## Features

- Allows preventing specific items from being recycled
- Allows multiplying recycler output (can use 0 to disable outputting specific items)
- Allows fully customizing recycler output
- Allows recycling custom items
- Allows outputting custom items

## Permissions

- `recyclemanager.admin` -- Allows all commands.

## Commands

- `recyclemanager.add <item id or short name>` -- Adds the specified item to the `Override output` section of the config.
- `recyclemanager.reset <item id or short name>` -- Adds or updates the specified item in the `Override output` section of the config.

## Configuration

Default configuration:

```json
{
  "Custom recycle speed": {
    "Enabled": false,
    "Recycle time (seconds)": 5.0
  },
  "Restricted input items": {
    "Item short names": [],
    "Item skin IDs": [],
    "Item display names (custom items)": []
  },
  "Max items in stack per recycle (% of max stack size)": {
    "Default percent": 10.0,
    "Percent by input item short name": {},
    "Percent by input item skin ID": {},
    "Percent by input item display name (custom items)": {}
  },
  "Output multipliers": {
    "Default multiplier": 1.0,
    "Multiplier by output item short name": {}
  },
  "Override output (before efficiency factor)": {
    "Override output by input item short name": {},
    "Override output by input item skin ID": {},
    "Override output by input item display name (custom items)": {}
  }
}
```

- `Custom recycle speed` -- This section allows you to customize recycler speed.
  - `Enabled` (`true` or `false`) -- While `true`, this plugin will override the recycling speed on all recyclers. Other plugins can use the `OnRecycleManagerSpeed` hook to override this behavior for specific recyclers or players.
  - `Recycle time (seconds)` -- While `Enabled` is `true`, this value determines how long (in seconds) recyclers will take to produce items. Vanilla equivalent is `5.0` seconds.
- `Restricted input items` -- This section allows you to designate specific items as **not** recyclable. Other plugins can use the `OnRecycleManagerItemRecyclable` hook to override this behavior for specific items.
  - `Item short names` -- Items with these short names cannot be recycled.
    - Example: `["rope", "sewingkit"]`
  - `Item skin IDs` -- Items with these skin IDs cannot be recycled.
    - Example: `[1234567890, 4567891230]`
  - `Item display names (custom items)` -- Items with these custom display names cannot be recycled. The name comparison is case-insensitive.
    - Example: `["Portable Minicopter", "Ultimate Fridge"]`
- `Max items per recycle` -- This section allows you to configure the max number of items that can be recycled at a time within a given stack of items. In vanilla, at most `10%` of the max stack size of an item can be recycled at a time. For example, if the `techparts` item has a max stack size of `50`, only `5` can be recycled at a time in vanilla.
  - `Default percent` -- This percentage applies to all items, except those that match one of the below short names, skin IDs, or display names. If you want to allow all items to be recycled while at max stack size, set this value to `100.0` and leave the other `Percent by ...` options blank.
  - `Percent by input item short name` -- This section allows you to override the percentage for specific items by item short name.
    - Example: `{ "gears": 100.0, "metalpipe": 100.0 }`
  - `Percent by input item skin ID` -- This section allows you to override the percentage for specific items by item skin ID.
    - Example: `{ "1234567890": 20.0, "0987654321": 20.0 }`
  - `Percent by input item display name (custom items)` -- This section allows you to override the percentage for specific items by item display name.
    - Example: `{ "Portable Car": 50.0, "Portable RHIB": 50.0 }`
- `Output multipliers` -- This section allows you to increase or decrease the output of specific items. For example, if you set `scrap` to `2.0`, all scrap output will be double.
  - `Default multiplier` -- This multipler applies to all items, except for those overriden in `Multiplier by output item short name`.
  - `Multiplier by output item short name` -- This section allows you to override the default multiplier for items by short name.
    - Example: `{ "scrap": 2.0, "metal.fragments": 0.0 }`
- `Override output` -- This section allows you to override the output of specific items. This can be used to replace the output of items that are already recyclable in vanilla, as well as to allow custom items to be recycled. The output you configure here will **not** be affected by `Output multipliers`, but it will be affected by recycling "efficiency" (the percentage of the crafting cost that is output by recycling), which is 50% in vanilla (plugins sometimes change it). For example, if you want an item to output `5` scrap, you should account for recycler efficiency by configuring that item to output `10` scrap.
  - `Override output by input item short name` -- This section allows you to define what the recycler will output for specific items by item short name. The output includes the following options.
    - `Item short name` -- The short name name of the output item.
    - `Item skin ID` -- The *optional* skin ID of the output item.
    - `Display name` -- The *optional* display name of the output item.
    - `Amount` -- The amount of the output item.
  - `Override output by input item skin ID` -- This section allows you to define what the recycler will output for specific items by item skin ID. Each entry here has the same options as `Override output by input item short name`.
  - `Override output by input item display name (custom items)` -- This section allows you to define what the recycler will output for specific items by item display name. Each entry here has the same options as `Override output by input item short name`.

#### Example configuration

```json
{
  "Custom recycle speed": {
    "Enabled": false,
    "Recycle time (seconds)": 5.0
  },
  "Restricted input items": {
    "Item short names": [
      "rope",
      "sewingkit"
    ],
    "Item skin IDs": [
      1234567890,
      4567891230
    ],
    "Item display names (custom items)": [
      "Portable Minicopter",
      "Ultimate Fridge"
    ]
  },
  "Max items in stack per recycle (% of max stack size)": {
    "Default percent": 10.0,
    "Percent by input item short name": {
      "gears": 100.0,
      "metalpipe": 100.0
    },
    "Percent by input item skin ID": {
      "1234567890": 20.0,
      "0987654321": 20.0
    },
    "Percent by input item display name (custom items)": {
      "Portable Car": 50.0,
      "Portable RHIB": 50.0
    }
  },
  "Output multipliers": {
    "Default multiplier": 2.0,
    "Multiplier by output item short name": {
      "wood": 4.0,
      "stones": 4.0,
      "metal.fragments": 4.0,
      "metal.refined": 4.0
    }
  },
  "Override output": {
    "Override output by input item short name": {
      "roadsigns": [
        {
          "Item short name": "paper",
          "Item skin ID": 2420097877,
          "Display name": "Cash",
          "Amount": 5.0
        },
        {
          "Item short name": "metal.refined",
          "Amount": 2.0
        }
      ],
      "techparts": [
        {
          "Item short name": "paper",
          "Item skin ID": 2420097877,
          "Display name": "Cash",
          "Amount": 20.0
        },
        {
          "Item short name": "metal.refined",
          "Amount": 2.0
        }
      ]
    },
    "Override output by input item skin ID": {
      "7894561230": [
        {
          "Item short name": "metal.fragments",
          "Amount": 100,
        },
        {
          "Item short name": "metal.refined",
          "Amount": 10,
        },
      ]
    },
    "Override output by input item display name (custom items)": {
      "Torpedo Launcher": [
        {
          "Item short name": "metal.fragments",
          "Amount": 500,
        },
        {
          "Item short name": "metal.refined",
          "Amount": 50,
        }
      ]
    }
  }
}
```

## Localization

```json
{
  "Error.NoPermission": "You don't have permission to do that.",
  "Error.Config": "Error: The config did not load correctly. Please fix the config and reload the plugin before running this command.",
  "Error.InvalidItem": "Error: Invalid item: <color=#fe0>{0}</color>",
  "Item.Syntax": "Syntax: <color=#fe0>{0} <item id or short name></color>",
  "Add.Exists": "Error: Item <color=#fe0>{0}</color> is already in the config. To reset that item to vanilla output, use <color=#fe0>recyclemanager.reset {0}</color>.",
  "Add.Success": "Successfully added item <color=#fe0>{0}</color> to the config.",
  "Reset.Success": "Successfully reset item <color=#fe0>{0}</color> in the config."
}
```

## FAQ

#### How can I spawn recyclers at monuments?

Use the plugin [Monument Addons](https://umod.org/plugins/monument-addons).

#### How can I allow players to place recyclers?

There are many plugins that allow that use case, including Extended Recycler, Portable Recycler and Home Recycler.

## Developer Hooks

#### OnRecycleManagerItemRecyclable

```cs
object OnRecycleManagerItemRecyclable(Item item, Recycler recycler)
```

- Called when this plugin is about to forcibly dictate whether an item is recyclable
- Returning `false` will prevent this plugin from dictating whether the item can be recycled
- Returning `null` will result in the default behavior
- This hook will not be called when this plugin aligns with vanilla on whether the item should be recyclable, because this plugin does not intervene in that situation

#### OnRecycleManagerSpeed

```cs
object OnRecycleManagerSpeed(Recycler recycler, BasePlayer player)
```

- Called when this plugin is about to change the speed of a recycler, after a player has toggled the recycler on
- Returning `false` will prevent this plugin from altering the recycler speed
- Returning `null` will result in the default behavior
- This hook will not be called when the custom speed feature is disabled

#### OnRecycleManagerRecycle

```cs
object OnRecycleManagerRecycle(Item item, Recycler recycler)
```

- Called when this plugin is about to recycle the item
- Returning `false` will prevent this plugin from controlling how the item is recycled
- Returning `null` will result in the default behavior

## Credits

- [**redBDGR**](https://umod.org/user/redBDGR) -- The original author of this plugin.
- [**Pho3niX90**](https://umod.org/user/Pho3niX90) -- Maintainer v1.0.16 - v1.1.2.
