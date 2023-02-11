## Video tutorial

[![Video Tutorial](https://img.youtube.com/vi/tvEw3aWjvFk/maxresdefault.jpg)](https://www.youtube.com/watch?v=tvEw3aWjvFk)

## Features

- Allows customizing recycler speed globally, in safe zones, per item, and according to permission
- Allows preventing specific items from being recycled
- Allows multiplying recycler output (can use 0 to disable outputting specific items)
- Allows fully customizing recycler output
- Allows recycling custom items
- Allows outputting custom items
- Administration panel allows previewing and editing recycler outputs in-game

## Permissions

- `recyclemanager.admin` -- Allows all commands, and allows using the administration panel while viewing a recycler.

### Speed permissions

The following permissions come with this plugin's **default configuration**. Granting one to a player alters the speed of recyclers they use.

- `recyclemanager.speed.fast` -- Recycles 5x as fast.
- `recyclemanager.speed.instant` -- Recycles instantly.

You can add more speed configurations in the plugin configuration (`Custom recycle speed` > `Speeds requiring permission`), and the plugin will automatically generate permissions of the format `recyclemanager.speed.<suffix>` when reloaded. If a player has permission to multiple presets, only the last one will apply  (based on the order in the config).

## Commands

- `recyclemanager.add <item id or short name>` -- Adds the specified item to the `Override output` section of the config.
- `recyclemanager.reset <item id or short name>` -- Adds or updates the specified item in the `Override output` section of the config.

## Configuration

Default configuration:

```json
{
  "Edit UI": {
    "Enabled": true
  },
  "Custom recycle speed": {
    "Enabled": false,
    "Default recycle time (seconds)": 5.0,
    "Recycle time multiplier while in safe zone": 1.0,
    "Recycle time multiplier by item short name (item: multiplier)": {},
    "Recycle time multiplier by permission": [
      {
        "Permission suffix": "fast",
        "Recycle time multiplier": 0.2
      },
      {
        "Permission suffix": "instant",
        "Recycle time multiplier": 0.0
      }
    ]
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
  "Override output": {
    "Override output by input item short name": {},
    "Override output by input item skin ID": {},
    "Override output by input item display name (custom items)": {}
  }
}
```

#### Edit UI

- `Edit UI`
  - `Enabled` (`true` or `false`) -- While `true`, players with the `recyclemanager.admin` permission will have access to the administration panel to preview and edit the recycle output of items. Set this to `false` if you don't intend on using the administration panel, to save a tiny bit on performance.

#### Recycler speed

- `Custom recycle speed` -- This section allows you to optionally customize recycler speed.
  - `Enabled` (`true` or `false`) -- While `true`, this plugin will override the recycling speed on all recyclers. While `false`, this plugin will not affect recycler speed. Default: `false`.
    - Note: If other plugins want to cooperate with or override this behavior for specific recyclers or players, they can use the the `OnRecycleManagerSpeed` hook. 
  - `Default recycle time (seconds)` -- This value determines how long (in seconds) recyclers will take to process each item. Vanilla equivalent is `5.0` seconds. Default: `5.0`.
  - `Recycle time multiplier while in safe zone` -- When a player starts a recycler while in a safe zone, recycle time will be multiplied by this value.
    - Example: `0.5` to double recycling speed in safe zones. Default: `1.0`.
  - `Recycle time multiplier by item short name (item: multiplier)` -- This section allows you to speed up or slow down recycling for specific input items, using multipliers.
    - Example: `{ "gears": 0.5, "metalpipe": 0.25 }` will make `gears` recycle in half the time (2x as fast), and `metalpipe` recycle in 1/4 the time (4x as fast).
  - `Recycle time multiplier by permission` -- This list allows you to speed up or slow down recycling for specific players according to their permission. Each entry in this list will generate a permission of the format `recyclemanager.speed.<suffix>`. Granting that permission to a player assigns the corresponding multiplier to them.
    - `Permission suffix` -- This is used to generate a permission of the format `recyclemanager.speed.<suffix>`. For example, if you set this to `"fast"`, the plugin will generate the permission `recyclemanager.speed.fast`.
    - `Recycle time multiplier` -- The time the recycler takes to process input items will be multiplied by this value.

Recycle time multiplier examples:
- `0.5` = 2x speed
- `0.25` = 4x speed
- `0.2` = 5x speed
- `0.1` = 10x speed
- `0.0` = instant

#### Item restrictions

- `Restricted input items` -- This section allows you to designate specific items as **not** recyclable. Other plugins can use the `OnRecycleManagerItemRecyclable` hook to override this behavior for specific items.
  - `Item short names` -- Items with these short names cannot be recycled.
    - Example: `["rope", "sewingkit"]`
  - `Item skin IDs` -- Items with these skin IDs cannot be recycled.
    - Example: `[1234567890, 4567891230]`
  - `Item display names (custom items)` -- Items with these custom display names cannot be recycled. The name comparison is case-insensitive.
    - Example: `["Portable Minicopter", "Ultimate Fridge"]`

#### Recycle stack speed

- `Max items per recycle` -- This section allows you to configure the max number of items that can be recycled at a time within a given stack of items. In vanilla, at most `10%` of the max stack size of an item can be recycled at a time. For example, if the `techparts` item has a max stack size of `50`, only `5` can be recycled at a time in vanilla.
  - `Default percent` -- This percentage applies to all items, except those that match one of the below short names, skin IDs, or display names. If you want to allow all items to be recycled while at max stack size, set this value to `100.0` and leave the other `Percent by ...` options blank (`{}`).
  - `Percent by input item short name` -- This section allows you to override the percentage for specific items by item short name.
    - Example: `{ "gears": 100.0, "metalpipe": 100.0 }`
  - `Percent by input item skin ID` -- This section allows you to override the percentage for specific items by item skin ID.
    - Example: `{ "1234567890": 20.0, "0987654321": 20.0 }`
  - `Percent by input item display name (custom items)` -- This section allows you to override the percentage for specific items by item display name.
    - Example: `{ "Portable Car": 50.0, "Portable RHIB": 50.0 }`

#### Output multipliers

- `Output multipliers` -- This section allows you to increase or decrease the output of specific items. For example, if you set `scrap` to `2.0`, all scrap output will be doubled.
  - `Default multiplier` -- This multipler applies to all items, except for those overriden in `Multiplier by output item short name`.
  - `Multiplier by output item short name` -- This section allows you to override the default multiplier for items by short name.
    - Example: `{ "scrap": 2.0, "metal.fragments": 0.0 }`

#### Custom output / custom recyclables

- `Override output` -- This section allows you to override the output of specific items. This can be used to replace the output of items that are already recyclable in vanilla, as well as to allow custom items to be recycled. The output you configure here will **not** be affected by `Output multipliers`.
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
  "Edit UI": {
    "Enabled": true
  },
  "Custom recycle speed": {
    "Enabled": true,
    "Default recycle time (seconds)": 5.0,
    "Recycle time multiplier while in safe zone": 1.0,
    "Recycle time multiplier by item short name (item: multiplier)": {
      "gears": 0.6,
      "metalpipe": 0.4
    },
    "Recycle time multiplier by permission": [
      {
        "Permission suffix": "fast",
        "Recycle time multiplier": 0.2
      },
      {
        "Permission suffix": "instant",
        "Recycle time multiplier": 0.0
      }
    ]
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
          "Amount": 100
        },
        {
          "Item short name": "metal.refined",
          "Amount": 10
        }
      ]
    },
    "Override output by input item display name (custom items)": {
      "Torpedo Launcher": [
        {
          "Item short name": "metal.fragments",
          "Amount": 500
        },
        {
          "Item short name": "metal.refined",
          "Amount": 50
        }
      ]
    }
  }
}
```

## Localization

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
object OnRecycleManagerSpeed(Recycler recycler, BasePlayer player, float[] recycleTime)
```

- Called after the player has toggled on the recycler
- Returning `false` will prevent this plugin from altering the recycler speed
- Returning `null` will result in the default behavior
- This hook will not be called when `Custom recycle speed` -> `Enabled` is `false`
- The `recycleTime` array has one item (at position `0`)
  - After this hook has been called on all subscribed plugins, if all plugins returned `null`, Recycle Manager will change the recycler time to `recycleTime[0]`
  - If you want to alter the recycle time, consider the current value, change it if necessary, then return `null`

```cs
// Example: Double recycle speed when a player starts their own personal recycler.
object OnRecycleManagerSpeed(Recycler recycler, BasePlayer player, float[] recycleTime)
{
    if (recycler.OwnerID == player.userID)
    {
        recycleTime[0] /= 2;
    }
    return null;
}
```

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
